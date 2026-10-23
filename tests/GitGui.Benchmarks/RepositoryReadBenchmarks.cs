#if NET10_0_OR_GREATER
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using GitGui.Infrastructure;
using Microsoft.Data.Sqlite;
using Perfolizer.Mathematics.OutlierDetection;

/// <summary>Existing, explicitly selected fixtures only; never clones or prepares repositories.</summary>
[MemoryDiagnoser]
[Config(typeof(RepositoryReadBenchmarkConfig))]
public class RepositoryReadBenchmarks
{
    public static void DescribeResolvedJobs()
    {
        // Conversion applies attributes/defaults/mutators, but never invokes
        // BenchmarkRunner, fixture setup, Git, or power-management APIs.
        using var benchmarks = BenchmarkConverter.TypeToBenchmarks(typeof(RepositoryReadBenchmarks));
        var jobs = benchmarks.BenchmarksCases.Select(benchmark => new
        {
            method = benchmark.Descriptor.WorkloadMethod.Name,
            benchmark = benchmark.DisplayInfo,
            powerPlanMode = benchmark.Job.Environment.PowerPlanMode,
            outliers = benchmark.Job.Accuracy.OutlierMode.ToString(),
            launches = benchmark.Job.Run.LaunchCount,
            warmups = benchmark.Job.Run.WarmupCount,
            iterations = benchmark.Job.Run.IterationCount,
            invocations = benchmark.Job.Run.InvocationCount,
            unroll = benchmark.Job.Run.UnrollFactor
        }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true }));
        if (jobs.Any(job => job.powerPlanMode != Guid.Empty || job.outliers != nameof(OutlierMode.DontRemove) ||
            job.invocations != 1 || job.unroll != 1 || job.launches != 1 || job.warmups != 1 || job.iterations != 5))
            throw new InvalidOperationException("Resolved read job violates the fixed-host read-cache benchmark protocol.");
    }

    [ParamsSource(nameof(ConfiguredFixtures))]
    public string Fixture { get; set; } = "";

    public IEnumerable<string> ConfiguredFixtures()
    {
        var configured = new[] { "Synthetic", "Real" }.Where(name =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FixtureVariable(name)))).ToArray();
        return configured.Length > 0 ? configured : throw new InvalidOperationException(
            "Set GITGUI_BENCH_SYNTHETIC and/or GITGUI_BENCH_REAL to existing owned fixtures. No repository is created automatically.");
    }

    private string root = "";
    private string executable = "";
    private string evidenceDirectory = "";
    private string head = "";
    private long headCommitCount;
    private DiffRequest diff = new("");
    private string? previousNoLazyFetch;
    private int iteration;
    private LibHistoryReader? native;
    private WorkspaceService workspace = null!;

    [GlobalSetup]
    public void DescribeFixture() => DescribeFixtureAsync().GetAwaiter().GetResult();

    private async Task DescribeFixtureAsync()
    {
        root = Path.GetFullPath(Environment.GetEnvironmentVariable(FixtureVariable(Fixture))
            ?? throw new InvalidOperationException("The selected fixture environment variable is missing."));
        executable = Environment.GetEnvironmentVariable("GITGUI_BENCH_GIT")
            ?? throw new InvalidOperationException("Set GITGUI_BENCH_GIT to the exact Git executable used for both fixtures (acceptance protocol: Git 2.56).");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        // Partial clones must not turn a read benchmark into an implicit network
        // fetch or object-store write. This changes only this benchmark process.
        previousNoLazyFetch = Environment.GetEnvironmentVariable("GIT_NO_LAZY_FETCH");
        Environment.SetEnvironmentVariable("GIT_NO_LAZY_FETCH", "1");
        var runtime = new GitRuntime();
        var installation = await runtime.SaveAsync(executable, default);
        executable = installation.Executable;
        var runner = new GitProcessRunner(runtime);
        root = Path.GetFullPath((await Read(runner, "rev-parse", "--show-toplevel")).Trim());
        head = (await Read(runner, "rev-parse", "--verify", "HEAD")).Trim();
        headCommitCount = long.Parse((await Read(runner, "rev-list", "--count", "HEAD")).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var files = await ReadBytes(runner, "ls-files", "-z");
        var origin = await runner.RunAsync(root, ["config", "--get", "remote.origin.url"], default,
            new() { Trusted = true, HideOutput = true });
        if (origin.ExitCode is not (0 or 1)) throw new GitCommandException(origin);
        diff = await SelectDiff(runner);
        evidenceDirectory = Path.Combine(Path.GetTempPath(), "gitgui-bdn-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidenceDirectory);
        var evidence = new
        {
            recordedAt = DateTimeOffset.UtcNow, fixture = Fixture, root, head, headCommitCount,
            trackedFileCount = files.LongCount(value => value == 0),
            origin = GitProcessRunner.Redact(Encoding.UTF8.GetString(origin.Output).Trim()),
            git = installation, diff,
            source = Environment.GetEnvironmentVariable("GITGUI_BENCH_SOURCE") ?? "working-tree",
            scope = "Engine reads. CompleteOpenFirst500 measures the entire OpenAsync task, including status/configuration/history; it is not the progressive first-history callback or a GUI first-frame measurement.",
            cachePolicy = "New WorkspaceService, LibHistoryReader, runner, runtime and temporary SQLite database for every iteration. Status and diff explicitly prime one OpenAsync outside measured work.",
            noLazyFetch = true
        };
        var metadata = Path.Combine(evidenceDirectory, "fixture.json");
        await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Fixture evidence: " + metadata);
    }

    [IterationSetup(Target = nameof(CompleteOpenFirst500))]
    public void NewColdService() => NewServiceAsync(prime: false).GetAwaiter().GetResult();

    [IterationSetup(Targets = new[] { nameof(RefreshStatus), nameof(ReadSelectedDiff) })]
    public void NewOpenedService() => NewServiceAsync(prime: true).GetAwaiter().GetResult();

    private async Task NewServiceAsync(bool prime)
    {
        var store = new WorkspaceStorage(Path.Combine(evidenceDirectory, "iteration-" + ++iteration + ".db"));
        await store.TrustAsync(root, default); // Trust is confined to this temporary benchmark database.
        var runtime = new GitRuntime(); await runtime.SaveAsync(executable, default);
        var runner = new GitProcessRunner(runtime);
        native = new LibHistoryReader();
        workspace = new WorkspaceService(runner, store, store, new SnapshotManager(runner, store), new ReadOnlyInteraction(), native, native);
        if (prime) _ = await workspace.OpenAsync(root, new(Take: 500), default);
    }

    [Benchmark(Description = "Complete OpenAsync (first 500; all state)")]
    public async Task<int> CompleteOpenFirst500()
    {
        var result = await workspace.OpenAsync(root, new(Take: 500), default);
        if (result.Commits.Count < Math.Min(500, headCommitCount))
            throw new InvalidOperationException("Open did not return the required first history page.");
        return result.Commits.Count;
    }

    [Benchmark(Description = "Status after OpenAsync (new service each iteration)")]
    public async Task<int> RefreshStatus() => (await workspace.RefreshStatusAsync(root, default)).Status.Files.Count;

    [Benchmark(Description = "Selected diff after OpenAsync (see fixture.json)")]
    public async Task<int> ReadSelectedDiff()
    {
        var result = await workspace.GetDiffAsync(root, diff, default);
        if (result.Length == 0) throw new InvalidOperationException("The selected benchmark diff is empty; choose a changed file or nonempty historical commit.");
        return result.Length;
    }

    [IterationCleanup]
    public void ReleaseIteration()
    {
        native?.Dispose(); native = null;
        SqliteConnection.ClearAllPools();
    }

    [GlobalCleanup]
    public void Finish()
    {
        ReleaseIteration();
        Environment.SetEnvironmentVariable("GIT_NO_LAZY_FETCH", previousNoLazyFetch);
        Console.WriteLine("Retained read benchmark metadata and isolated databases: " + evidenceDirectory);
    }

    private async Task<DiffRequest> SelectDiff(GitProcessRunner runner)
    {
        var selected = Environment.GetEnvironmentVariable(FixtureVariable(Fixture) + "_DIFF_PATH");
        if (Fixture == "Synthetic")
            return new(ValidatePath(selected ?? "big.txt"));
        if (selected is not null) return new(ValidatePath(selected), Commit: head);
        var parents = (await Read(runner, "rev-list", "--parents", "-n", "1", head)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var arguments = new List<string> { "diff-tree", "--no-commit-id", "--name-only", "-z", "-r", "--no-renames" };
        if (parents.Length > 1) arguments.Add(parents[1].Trim()); else arguments.Add("--root");
        arguments.Add(head); arguments.Add("--");
        var bytes = await ReadBytes(runner, arguments.ToArray());
        var path = new UTF8Encoding(false, true).GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new InvalidOperationException("HEAD has no changed path. Set GITGUI_BENCH_REAL_DIFF_PATH for a meaningful historical diff.");
        return new(ValidatePath(path), Commit: head);
    }

    private static string ValidatePath(string path)
    {
        if (!GitPathPolicy.IsRepresentable(path) || Path.IsPathRooted(path) || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidOperationException("Benchmark diff paths must be representable repository-relative Git paths.");
        return path;
    }

    private static string FixtureVariable(string name) => "GITGUI_BENCH_" + name.ToUpperInvariant();
    private async Task<string> Read(GitProcessRunner runner, params string[] arguments)
        => Encoding.UTF8.GetString(await ReadBytes(runner, arguments));
    private async Task<byte[]> ReadBytes(GitProcessRunner runner, params string[] arguments)
    {
        var result = await runner.RunAsync(root, arguments, default, new() { Trusted = true, HideOutput = true });
        if (result.ExitCode != 0) throw new GitCommandException(result);
        return result.Output;
    }

    private sealed class ReadOnlyInteraction : IWorkspaceInteraction
    {
        public Task<ConfirmationAnswer> ConfirmAsync(Confirmation confirmation, CancellationToken ct)
            => throw new InvalidOperationException("Read benchmarks cannot authorize repository writes.");
        public Task<string?> CredentialAsync(string prompt, CancellationToken ct)
            => throw new InvalidOperationException("Read benchmarks cannot request network credentials.");
    }
}

public sealed class RepositoryReadBenchmarkConfig : ManualConfig
{
    public RepositoryReadBenchmarkConfig()
    {
        AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(1).WithIterationCount(5)
            .WithInvocationCount(1).WithUnrollFactor(1).WithOutlierMode(OutlierMode.DontRemove)
            .DontEnforcePowerPlan().AsDefault());
        // Command-line jobs are created before per-type default configuration
        // is merged. Enforce invariants on those jobs too, without adding runs.
        AddJob(Job.Default.WithInvocationCount(1).WithUnrollFactor(1)
            .WithOutlierMode(OutlierMode.DontRemove).DontEnforcePowerPlan().AsMutator());
        AddExporter(JsonExporter.Full);
    }
}
#endif
