using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using GitGui.Infrastructure;

internal static class LargeRepositoryBenchmark
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static async Task<GitProcessRunner> Runner()
    {
        var runtime = new GitRuntime();
        await runtime.SaveAsync(Environment.GetEnvironmentVariable("GITGUI_BENCH_GIT"), default);
        return new(runtime);
    }
    private static async Task<string> Git(GitProcessRunner runner, string root, params string[] arguments)
    {
        var result = await runner.RunAsync(root, arguments, default, new() { Trusted = true, IsReadOnly = false });
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
        return Encoding.UTF8.GetString(result.Output).Trim();
    }
    public static async Task Prepare(string path, int commits, int files)
    {
        if (commits < 2 || files < 3) throw new ArgumentOutOfRangeException(nameof(commits));
        var root = Path.GetFullPath(path);
        if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("Fixture destination must be new; no existing data is replaced.");
        Directory.CreateDirectory(root);
        var runner = await Runner();
        await Git(runner, root, "init", "-b", "main", "--object-format=sha1");
        await Git(runner, root, "config", "core.autocrlf", "false");
        await Git(runner, root, "config", "core.hooksPath", Path.Combine(root, ".git", "hooks"));
        await Git(runner, root, "config", "gc.auto", "0");
        await Git(runner, root, "config", "core.untrackedCache", "true");
        if (OperatingSystem.IsWindows()) await Git(runner, root, "config", "core.fscache", "true");
        var text = string.Join('\n', Enumerable.Range(0, 10_000).Select(i => $"original line {i:D5}")) + "\n";
        using var input = new MemoryStream();
        using (var writer = new StreamWriter(input, new UTF8Encoding(false), leaveOpen: true))
        {
            void Data(string value) { writer.Write("data " + Encoding.UTF8.GetByteCount(value) + "\n"); writer.Write(value); writer.Write('\n'); }
            writer.Write("blob\nmark :1\n"); Data("fixture\n");
            writer.Write("blob\nmark :2\n"); Data(text);
            writer.Write("commit refs/heads/main\nmark :1000\ncommitter Benchmark <benchmark@example.invalid> 1700000000 +0000\n"); Data("fixture initial\n");
            for (var i = 0; i < files - 2; i++) writer.Write($"M 100644 :1 bucket-{i / 500:D3}/file-{i:D6}.txt\n");
            writer.Write("M 100644 :1 timeline.txt\nM 100644 :2 big.txt\n\n");
            for (var i = 1; i < commits; i++)
            {
                writer.Write($"blob\nmark :{1_000_000 + i}\n"); Data(i + "\n");
                writer.Write($"commit refs/heads/main\nmark :{1000 + i}\ncommitter Benchmark <benchmark@example.invalid> {1700000000 + i} +0000\n");
                Data("history " + i + "\n"); writer.Write($"from :{999 + i}\nM 100644 :{1_000_000 + i} timeline.txt\n\n");
            }
            writer.Write("done\n");
        }
        var imported = await runner.RunAsync(root, ["fast-import", "--quiet", "--done"], default,
            new() { Trusted = true, IsReadOnly = false, Input = input.ToArray(), HideOutput = true });
        if (imported.ExitCode != 0) throw new InvalidOperationException(imported.Error);
        await Git(runner, root, "reset", "--hard", "HEAD");
        await Git(runner, root, "repack", "-adq");
        await Git(runner, root, "commit-graph", "write", "--reachable");
        var actualCommits = int.Parse(await Git(runner, root, "rev-list", "--count", "HEAD"));
        var actualFiles = (await Git(runner, root, "ls-files", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
        if (actualCommits != commits || actualFiles != files) throw new InvalidOperationException("Fixture shape does not match requested counts.");
        var fsmonitor = false;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var start = await runner.RunAsync(root, ["fsmonitor--daemon", "start"], default, new() { Trusted = true, IsReadOnly = false });
            if (start.ExitCode == 0)
            {
                await Git(runner, root, "config", "core.fsmonitor", "true");
                fsmonitor = (await runner.RunAsync(root, ["fsmonitor--daemon", "status"], default)).ExitCode == 0;
            }
        }
        // Prime FSMN/UNTR once with normal Git index writes. Product read-only
        // refreshes intentionally do not write these optional index extensions.
        await Git(runner, root, "status", "--porcelain=v2", "-z", "--untracked-files=all");
        // A 10k-line file with100 changes is shared by baseline and candidate.
        await File.WriteAllTextAsync(Path.Combine(root, "big.txt"), string.Join('\n', Enumerable.Range(0, 10_000)
            .Select(i => i % 100 == 0 ? $"changed line {i:D5}" : $"original line {i:D5}")) + "\n", new UTF8Encoding(false));
        var manifest = new { schema = 1, protocol = 2, fsmonitorPrimed = fsmonitor, windowsFileSystemCache = OperatingSystem.IsWindows(), root, commits, files, diffLines = 10_000, changedLines = 100,
            head = await Git(runner, root, "rev-parse", "HEAD"), git = await Git(runner, root, "--version"), fsmonitor,
            fingerprint = Guid.NewGuid().ToString("N") };
        await File.WriteAllTextAsync(root + ".fixture.json", JsonSerializer.Serialize(manifest, Json));
        Console.WriteLine(JsonSerializer.Serialize(manifest, Json));
    }
    public static async Task Measure(string path, string outputPath)
    {
        var root = Path.GetFullPath(path); var output = Path.GetFullPath(outputPath);
        if (File.Exists(output)) throw new InvalidOperationException("Benchmark output must be new.");
        var manifest = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(root + ".fixture.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var runner = await Runner(); var commands = new System.Collections.Concurrent.ConcurrentQueue<CommandEntry>(); runner.CommandExecuted += (_, e) => commands.Enqueue(e);
        var store = new WorkspaceStorage(output + ".db"); await store.TrustAsync(root, default);
        var native = new LibHistoryReader(); var snapshots = new SnapshotManager(runner, store);
        var workspace = new WorkspaceService(runner, store, store, snapshots, new ReadOnlyInteraction(), native, native);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        double? publishedMs = null;
        int? publishedCommands = null;
#if NET10_0_OR_GREATER
        var first = await workspace.OpenWithHistoryAsync(root, new(Take: 500), preview =>
        {
            if (preview.Page.Commits.Count != 500 && manifest.GetProperty("commits").GetInt32() >= 500)
                throw new InvalidOperationException("Published graph page is not500 commits.");
            publishedMs = clock.Elapsed.TotalMilliseconds; publishedCommands = commands.Count;
            return Task.CompletedTask;
        }, default);
#else
        var first = await workspace.OpenAsync(root, new(Take: 500), default);
#endif
        var openTotalReadyMs = clock.Elapsed.TotalMilliseconds;
        var openMs = publishedMs ?? openTotalReadyMs;
        if (first.Commits.Count != 500 && manifest.GetProperty("commits").GetInt32() >= 500) throw new InvalidOperationException("First graph page is not500 commits.");
        var firstCommands = publishedCommands ?? commands.Count;
        var status = new List<double>(); var diffs = new List<double>(); var graph = new List<double>();
        var statusCommands = new List<int>();
        const int layoutIterations = 1000;
        // Tiered JIT must reach steady state for sub-millisecond layout comparisons.
        for (var i = 0; i < 2000; i++) _ = CommitGraphLayout.Build(first.Commits);
        for (var i = 0; i < 6; i++)
        {
            var before = commands.Count; clock.Restart();
#if NET10_0_OR_GREATER
            _ = await workspace.RefreshStatusAsync(root, default);
#else
            _ = await workspace.OpenAsync(root, new(Take: 500), default);
#endif
            var elapsed = clock.Elapsed.TotalMilliseconds;
            if (i > 0) { status.Add(elapsed); statusCommands.Add(commands.Count - before); }
            clock.Restart(); var diff = await workspace.GetDiffAsync(root, new("big.txt"), default);
            if (!diff.Contains("changed line", StringComparison.Ordinal)) throw new InvalidOperationException("Fixture diff is missing.");
            if (i > 0) diffs.Add(clock.Elapsed.TotalMilliseconds);
            clock.Restart();
            for (var layout = 0; layout < layoutIterations; layout++) _ = CommitGraphLayout.Build(first.Commits);
            if (i > 0) graph.Add(clock.Elapsed.TotalMilliseconds / layoutIterations);
        }
        static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
        var report = new { schema = 1, recordedAt = DateTimeOffset.UtcNow, fixture = manifest,
            source = Environment.GetEnvironmentVariable("GITGUI_BENCH_SOURCE") ?? "working-tree",
            binaries = new[] { "GitGui.Core.dll", "GitGui.Engine.Cli.dll", "GitGui.Engine.Lib.dll", "GitGui.Benchmarks.dll" }
                .ToDictionary(name => name, name => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name)))).ToLowerInvariant()),
            machine = new { name = Environment.MachineName, os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
                runtime = RuntimeInformation.FrameworkDescription },
            metrics = new Dictionary<string, double> { ["openFirst500Ms"] = openMs, ["statusMedianMs"] = Median(status),
                ["diff10kLinesMedianMs"] = Median(diffs), ["layout500MedianMs"] = Median(graph) },
            samples = new { status, diff = diffs, layout = graph, layoutIterations, statusCommandCounts = statusCommands }, firstCommands,
            openTotalReadyMs, firstGraphBoundary = publishedMs is null ? "Full workspace returned (backend has no progressive history)" : "First history page published; complete workspace readiness retained separately",
            allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, processWorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
            scope = "Engine reads and graph layout only. Desktop startup, scrolling frames, UI blocking and app resident memory require separate native desktop instrumentation." };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, Json));
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
        if ((object)native is IDisposable disposable) disposable.Dispose();
    }
    private sealed class ReadOnlyInteraction : IWorkspaceInteraction
    {
        public Task<ConfirmationAnswer> ConfirmAsync(Confirmation confirmation, CancellationToken ct) => throw new InvalidOperationException("Read benchmarks must not mutate repositories.");
        public Task<string?> CredentialAsync(string prompt, CancellationToken ct) => throw new InvalidOperationException("Benchmarks must not access network credentials.");
    }
}
