using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Logging;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Transport;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitGui.Controls;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Infrastructure;
using GitGui.UI;

namespace GitGui.App;

/// <summary>Opt-in, visible native desktop measurement. Never opens user settings or an arbitrary repository.</summary>
internal sealed class DesktopPerformanceProbe(string reportPath, string dataPath, string? repository)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly DateTime processStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private readonly NativePerformanceMetrics nativeMetrics = new();
    private readonly ConcurrentQueue<object> startupCheckpoints = new();
    private readonly ConcurrentQueue<object> surfaceBarrierWindowFailures = new();
    public string DataPath { get; } = dataPath;
    public bool SoftwareRendering { get; private init; }
    private CompositionProbeSettings Composition { get; init; } = new(false);
    public bool DirectComposition => Composition.Direct;

    public void StartupCheckpoint(string checkpoint) => startupCheckpoints.Enqueue(new
    {
        checkpoint, recordedQpc = Stopwatch.GetTimestamp(), processElapsedMs = (DateTime.UtcNow - processStarted).TotalMilliseconds
    });

    public static DesktopPerformanceProbe? Parse(string[] args)
    {
        if (!args.Any(a => a.StartsWith("--performance-", StringComparison.Ordinal))) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--performance-report" or "--performance-data" or "--performance-repository") || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Performance arguments must be unique named path/value pairs.");
        }
        if (!values.TryGetValue("--performance-report", out var report) || !values.TryGetValue("--performance-data", out var data))
            throw new ArgumentException("Performance probes require a NEW report file and NEW isolated data directory.");
        report = Path.GetFullPath(report); data = Path.GetFullPath(data);
        var repo = values.TryGetValue("--performance-repository", out var supplied) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(supplied)) : null;
        if (File.Exists(report) || Directory.Exists(report) || File.Exists(data) || Directory.Exists(data))
            throw new ArgumentException("Performance report and data paths must not already exist.");
        if (repo is not null && (Within(report, repo) || Within(data, repo)))
            throw new ArgumentException("Probe output and isolated settings must be outside the fixture.");
        if (Within(report, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitGui")) || Within(data, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitGui")))
            throw new ArgumentException("Performance probes cannot use normal application settings storage.");
        var renderer = Environment.GetEnvironmentVariable("GITGUI_BENCH_RENDERER");
        if (renderer is not (null or "" or "default" or "software")) throw new ArgumentException("Probe renderer must be default or software.");
        var composition = CompositionProbeSettings.Parse(Environment.GetEnvironmentVariable("GITGUI_BENCH_COMPOSITION"), renderer == "software", OperatingSystem.IsWindows());
        Directory.CreateDirectory(data);
        // Probe child Git commands have no user/system configuration. Fixture-local settings are validated before trust.
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(key, null);
        var emptyConfig = Path.Combine(data, "empty.gitconfig"); File.WriteAllText(emptyConfig, "");
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1"); Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", emptyConfig);
        // Must precede the first Avalonia initialization; this switch and listener only exist in a probe process.
        AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", true);
        var probe = new DesktopPerformanceProbe(report, data, repo) { SoftwareRendering = renderer == "software" && OperatingSystem.IsWindows(), Composition = composition };
        probe.nativeMetrics.BindUiThread();
        return probe;
    }

    private static bool Within(string path, string directory)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return path.Equals(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            path.StartsWith(prefix + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public void Attach(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainWindowViewModel vm,
        CommitGraphControl graph, GitProcessRunner runner, WorkspaceStorage storage, IGitRuntimeSettings runtime)
    {
        nativeMetrics.BindUiThread();
        window.Opened += async (_, _) => await RunAsync(desktop, window, vm, graph, runner, storage, runtime);
    }

    private async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainWindowViewModel vm,
        CommitGraphControl graph, GitProcessRunner runner, WorkspaceStorage storage, IGitRuntimeSettings runtime)
    {
        using var trace = new ProbeTrace(vm, nativeMetrics);
        using var renderingErrors = new RenderErrorObserver();
        var processOriginQpc = Stopwatch.GetTimestamp() - (long)((DateTime.UtcNow - processStarted).TotalSeconds * Stopwatch.Frequency);
        var values = new Dictionary<string, object?>
        {
            ["schema"] = 1, ["recordedAt"] = DateTimeOffset.UtcNow,
            ["timelineOriginUtc"] = trace.OriginUtc,
            ["timelineOriginQpc"] = trace.OriginQpc,
            ["renderingModeRequested"] = SoftwareRendering ? "Windows Software framebuffer" : "Platform default (Windows ANGLE EGL with Software fallback)",
            ["compositionModeRequested"] = Composition.Requested,
            ["host"] = new { os = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
                configuration = typeof(DesktopApp).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
                debuggerAttached = Debugger.IsAttached },
            ["measurementBoundary"] = new
            {
                startup = "OS Process.StartTime through initialization and a requested CompositionBatch.Rendered completion paired with a genuine render histogram after visible layout. Includes runtime, DI and layout startup. Original RAF/initialization times remain diagnostic. Canonical surface submission means drawing sessions returned; not GPU, OS compositor or physical display completion.",
                graph = "Repository-path assignment through first500 publication, queued graph rebuild/LayoutReady, visible History layout, RAF diagnostic, then a requested rendered batch and native surface-update return. Full refresh completion remains separate. Setup/validation excluded. Surface submission is a conservative completed batch, not physical display.",
                status = "RefreshStatusAsync through Changes.Reset, visible working-tree layout, diagnostic RAF and completed rendered batch/surface update. Total status+selected-file diff/conflict readiness is retained separately.",
                diff = "Selecting generated big.txt after clearing selection through IsDiffLoading=false, active inline editor equality, layout, diagnostic RAF and completed rendered batch/surface update. Includes Git read, projection, bindings and editor layout; source file has10000lines.",
                frames = "Actual native RequestAnimationFrame callback intervals while automation ScrollProvider changes the visible commit graph once per callback; not headless, synthetic frame scheduling or GPU presentation timing.",
                scrollRendering = "Real avalonia.comp.render.time scopes correlated with distinct public CompositionBatch.Rendered notifications after drawing-context/session disposal and native surface EndDraw. Exact QPC interval excludes drain and report work. This measures application rendering plus returned surface-update submission, not GPU completion, Windows compositor commit completion, physical display or photons. Histogram scopes end before surface disposal; batch notifications occur in finally, so render errors are captured separately and invalidate the gate. Nominal60 includes60000/1001Hz; endpoint allowance is floor(duration*60000/1001)-1, with every internal gap>=1.5frames failing even if average recovers.",
                heartbeat = "Worker timer to UI Dispatcher callback delay during product open/status/diff/scroll or idle sampling, including OS scheduling and dispatcher backlog. Excludes probe runtime/fixture validation/trust setup and startup. This estimates responsiveness, not exact duration of every UI callback.",
                instrumentation = "Ordered heartbeat timestamps/phase, worker16ms timer elapsed and GC deltas are retained. Bound-property spans measure VM PropertyChanging through PropertyChanged observers registered after normal bindings; they include synchronous setter/earlier-subscriber work, exclude deferred layout and are not a full UI callback profiler. Explicit UpdateLayout spans are separate. Instrumentation does not change renderer/GC/thread-pool settings.",
                avaloniaMetrics = "Avalonia 11.3.22 built-in Diagnostic.Meter histograms for layout measure/arrange/render/input and compositor update/render, enabled before framework startup only in this probe. Every measurement is retained without a duration threshold. RecordedQpc is the synchronous measurement callback timestamp (after the measured scope), not its start; Phase is the phase at that callback. NativeThreadId is GetCurrentThreadId on Windows, otherwise null. Observable counters are sampled once at report time. These scopes do not cover every DispatcherOperation; neither these samples nor heartbeat delays prove that every UI callback is <=16ms. Collection itself adds diagnostic overhead.",
                memory = "Worker samples Process.WorkingSet64 every100ms throughout exact five-second graph scrolling, with samples bracketing both QPC endpoints; all samples and maximum retained. Ending working set is separate; no forced GC or per-frame UI process queries."
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var heartbeats = new List<HeartbeatSample>();
        Task heartbeat = Task.CompletedTask; var exitCode = 0;
        try
        {
            await FrameAsync(window, timeout.Token);
            values["startupFirstNativeFrameCallbackMs"] = (DateTime.UtcNow - processStarted).TotalMilliseconds;
            if (vm.InitializeCommand.ExecutionTask is { } initialization) await initialization;
            values["startupInteractiveAfterInitializationMs"] = (DateTime.UtcNow - processStarted).TotalMilliseconds;
            var startupSurface = await SurfaceSubmissionAsync(window, processOriginQpc, timeout.Token);
            values["startupSurfaceInteractiveMs"] = Stopwatch.GetElapsedTime(processOriginQpc, startupSurface.CompletedQpc).TotalMilliseconds;
            values["startupSurfaceSubmission"] = startupSurface;
            trace.SetPhase("probe-setup"); var setup = Stopwatch.StartNew();
            var git = await Task.Run(() => runtime.SaveAsync(Environment.GetEnvironmentVariable("GITGUI_BENCH_GIT"), timeout.Token), timeout.Token);
            values["gitRuntime"] = git;
            if (repository is not null)
            {
                var fixture = await Task.Run(async () =>
                {
                    var validated = await ValidateFixtureAsync(repository, runner, git.Version, timeout.Token);
                    await storage.TrustAsync(repository, timeout.Token); return validated;
                }, timeout.Token);
                values["fixture"] = fixture;
                values["probeSetupValidationMs"] = setup.Elapsed.TotalMilliseconds;
                heartbeat = HeartbeatAsync(heartbeats, trace, heartbeatCancellation.Token);
                var opening = await CollectionFrameAsync(window, vm.Commits, () =>
                    {
                        vm.RepositoryPath = repository; window.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = 1;
                        return vm.RefreshCommand.ExecuteAsync(null);
                    },
                    async () => await graph.LayoutReady, () => vm.Commits.Count == 500, trace, "open500", timeout.Token);
                if (!vm.HasRepository || vm.Commits.Count != 500) throw new InvalidOperationException("The real GUI did not open exactly the first 500 commits: " + vm.Notice);
                values["openFirst500GraphFrameCallbackMs"] = opening.FrameCallbackMs; values["openTotalReadyMs"] = opening.TotalReadyMs; values["openPhases"] = opening;
                values["openFirst500GraphSurfaceSubmissionMs"] = opening.SurfaceSubmissionMs;
                trace.SetPhase("show-status-tab"); window.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = 0;
                var status = new List<UiPhase>();
                for (var i = 0; i < 3; i++) status.Add(await CollectionFrameAsync(window, vm.Changes, vm.RefreshStatusAsync, null, () => true, trace, "status" + (i + 1), timeout.Token));
                values["statusListFrameCallbackMs"] = Statistics(status.Select(s => s.FrameCallbackMs));
                values["statusListSurfaceSubmissionMs"] = Statistics(status.Select(s => s.SurfaceSubmissionMs));
                values["statusRefreshMs"] = Statistics(status.Select(s => s.TotalReadyMs)); values["statusPhases"] = status;
                window.FindControl<TabControl>("DetailTabs")!.SelectedIndex = 0; vm.SplitDiff = false;
                var diff = new List<DiffPhase>();
                for (var i = 0; i < 3; i++) diff.Add(await DiffFrameAsync(window, vm, trace, "diff" + (i + 1), timeout.Token));
                values["diffSelectionFrameCallbackMs"] = Statistics(diff.Select(d => d.FrameCallbackMs));
                values["diffSelectionSurfaceSubmissionMs"] = Statistics(diff.Select(d => d.SurfaceSubmissionMs)); values["diffPhases"] = diff;
                values["diffSourceFileLines"] = fixture.GetProperty("diffLines").GetInt32(); values["diffDisplayedPatchLines"] = vm.Diff.Count(c => c == '\n');
                trace.SetPhase("load-more"); window.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = 1;
                await vm.LoadMoreCommand.ExecuteAsync(null);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, timeout.Token);
                await graph.LayoutReady; values["loadedCommitsDuringScroll"] = vm.Commits.Count;
                var scroll = ControlAutomationPeer.CreatePeerForElement(graph)!.GetProvider<IScrollProvider>()!;
                var scrolling = await SampleScrollFramesAsync(window, () => scroll.SetScrollPercent(-1, (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency * 27) % 100), trace, timeout.Token);
                values["scrollNativeFrameIntervalsMs"] = scrolling.Callbacks;
                values["scrollRendering"] = scrolling.Rendering;
                values["steadyMemory"] = scrolling.Memory;
            }
            else
            {
                values["probeSetupValidationMs"] = setup.Elapsed.TotalMilliseconds;
                trace.SetPhase("idle"); heartbeat = HeartbeatAsync(heartbeats, trace, heartbeatCancellation.Token);
                values["idleNativeFrameIntervalsMs"] = (await SampleFramesAsync(window, () => window.InvalidateVisual(), TimeSpan.FromSeconds(2), timeout.Token)).Report;
            }
            using var process = Process.GetCurrentProcess(); process.Refresh(); values["workingSetBytes"] = process.WorkingSet64; values["privateBytes"] = process.PrivateMemorySize64;
        }
        catch (Exception ex) { exitCode = 1; values["failure"] = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            heartbeatCancellation.Cancel(); try { await heartbeat; } catch (OperationCanceledException) { }
            // Finish the actual product callback before the probe serializes its
            // diagnostic report. Keep every profiler span and the same thresholds;
            // report hashing/JSON must not extend an otherwise finished Git UI job.
            trace.SetPhase("probe-report");
            await Task.Yield();
            values["uiDispatcherHeartbeatDelayMs"] = Statistics(heartbeats.Select(s => s.QueueDelayMs));
            values["heartbeatSamplesInOrder"] = heartbeats.ToArray();
            values["phaseTimeline"] = trace.Phases.ToArray();
            values["uiSynchronousSpans"] = trace.Spans.ToArray();
            values["startupCheckpoints"] = startupCheckpoints.ToArray();
            values["surfaceBarrierWindowFailures"] = surfaceBarrierWindowFailures.ToArray();
            var surfaceErrors = renderingErrors.Snapshot();
            if (!renderingErrors.IsStillInstalled) surfaceErrors = [.. surfaceErrors, new(Stopwatch.GetTimestamp(), "Avalonia error observer was replaced during measurement.")];
            values["surfaceSubmissionErrors"] = surfaceErrors;
            if (surfaceErrors.Length > 0 && exitCode == 0) exitCode = 1;
            nativeMetrics.RecordObservableInstruments();
            nativeMetrics.Dispose();
            values["avaloniaMetrics"] = new
            {
                meter = NativePerformanceMetrics.MeterName, timestampFrequency = nativeMetrics.TimestampFrequency,
                uiManagedThreadId = nativeMetrics.UiManagedThreadId, uiNativeThreadId = nativeMetrics.UiNativeThreadId,
                instruments = nativeMetrics.Instruments(), samplesInOrder = nativeMetrics.Snapshot()
            };
            try
            {
                // Report-only provenance: no product file reads/hashing before
                // startup, operation, scroll or working-set measurements end.
                var backend = ObserveCompositionBackend();
                values["compositionBackend"] = backend;
                if (DirectComposition && backend.RequestedBackendMatched != true)
                {
                    values["compositionFailure"] = "DirectComposition with D3D11 ANGLE was requested, but the actual registered backend differs.";
                    if (exitCode == 0) exitCode = 1;
                }
                var loaded = AppDomain.CurrentDomain.GetAssemblies();
                values["productAssemblies"] = new[] { "GitGui.App", "GitGui.Core", "GitGui.Controls", "GitGui.Engine.Cli", "GitGui.Engine.Lib", "GitGui.Infrastructure", "GitGui.UI" }
                    .ToDictionary(name => name, name =>
                    {
                        var assembly = loaded.Single(a => a.GetName().Name == name);
                        using var stream = File.OpenRead(assembly.Location);
                        return new { fileName = Path.GetFileName(assembly.Location), sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant(),
                            mvid = assembly.ManifestModule.ModuleVersionId.ToString("D"), version = assembly.GetName().Version!.ToString() };
                    });
            }
            catch (Exception exception) { values["provenanceFailure"] = exception.GetType().Name + ": " + exception.Message; if (exitCode == 0) exitCode = 1; }
            values["completed"] = exitCode == 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                await using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
                await JsonSerializer.SerializeAsync(output, values, Json);
            }
            catch { exitCode = 2; }
            desktop.Shutdown(exitCode);
        }
    }

    private CompositionBackendEvidence ObserveCompositionBackend()
    {
        if (!OperatingSystem.IsWindows()) return Composition.Observe(null, null);
        // Version-pinned diagnostic reflection. Read already registered constants only,
        // after all product timing/working-set samples; never initialize a backend here.
        var assembly = typeof(AvaloniaObject).Assembly;
        var locator = assembly.GetType("Avalonia.AvaloniaLocator", throwOnError: true)!;
        var resolver = locator.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var getService = assembly.GetType("Avalonia.IAvaloniaDependencyResolver", throwOnError: true)!.GetMethod("GetService")!;
        var graphics = getService.Invoke(resolver, [typeof(Avalonia.Platform.IPlatformGraphics)]);
        var factoryType = typeof(Win32PlatformOptions).Assembly.GetType("Avalonia.Win32.IWindowsSurfaceFactory", throwOnError: true)!;
        var factory = getService.Invoke(resolver, [factoryType]);
        return Composition.Observe(graphics?.GetType().FullName, factory?.GetType().FullName);
    }

    private sealed record SurfaceSubmission(long StartedQpc, long RequestedQpc, long SceneUpdatedQpc, long CompletedQpc, long RenderPassQpc, double RenderDurationMs, bool CompletedSuccessfully, string RepaintTargetType)
    {
        public object? ProbeInkMarker { get; init; }
    }
    private sealed record UiPhase(double PublishedMs, double FrameCallbackMs, double SurfaceSubmissionMs, double TotalReadyMs, SurfaceSubmission SurfaceSubmission);
    private sealed record DiffPhase(double FrameCallbackMs, double SurfaceSubmissionMs, SurfaceSubmission SurfaceSubmission);
    private async Task<UiPhase> CollectionFrameAsync<T>(Window window, ObservableCollection<T> collection, Func<Task> start,
        Func<Task>? waitLayout, Func<bool> acceptsPublication, ProbeTrace trace, string phase, CancellationToken ct)
    {
        trace.SetPhase(phase + ".wait-publication");
        var startedQpc = Stopwatch.GetTimestamp(); var clock = Stopwatch.StartNew(); var publication = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Published(object? sender, NotifyCollectionChangedEventArgs e) { if (acceptsPublication()) publication.TrySetResult(clock.Elapsed.TotalMilliseconds); }
        collection.CollectionChanged += Published;
        try
        {
            var operation = start(); await Task.WhenAny(publication.Task, operation).WaitAsync(ct);
            if (!publication.Task.IsCompleted) { await operation; throw new InvalidOperationException("The GUI operation finished without the expected collection publication."); }
            var publishedMs = await publication.Task;
            trace.SetPhase(phase + ".published-await-bindings");
            // A collection observer can run before the graph's queued rebuild; wait for that UI turn, then its actual layout task.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
            if (waitLayout is not null) await waitLayout().WaitAsync(ct);
            trace.SetPhase(phase + ".layout"); trace.Measure("Window.UpdateLayout", window.UpdateLayout);
            trace.SetPhase(phase + ".await-native-frame"); await FrameAsync(window, ct); var frame = clock.Elapsed.TotalMilliseconds;
            trace.SetPhase(phase + ".await-surface-submission"); var surface = await SurfaceSubmissionAsync(window, startedQpc, ct);
            trace.SetPhase(phase + ".remainder"); await operation.WaitAsync(ct);
            trace.SetPhase(phase + ".complete"); return new(publishedMs, frame, Stopwatch.GetElapsedTime(startedQpc, surface.CompletedQpc).TotalMilliseconds, clock.Elapsed.TotalMilliseconds, surface);
        }
        finally { collection.CollectionChanged -= Published; }
    }

    private async Task<DiffPhase> DiffFrameAsync(Window window, MainWindowViewModel vm, ProbeTrace trace, string phase, CancellationToken ct)
    {
        trace.SetPhase(phase + ".clear-selection");
        var target = vm.Changes.Single(f => f.Path == "big.txt"); vm.SelectedChange = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Loaded(object? sender, PropertyChangedEventArgs e)
        { if (e.PropertyName == nameof(vm.IsDiffLoading) && !vm.IsDiffLoading && vm.SelectedChange?.Path == target.Path) completion.TrySetResult(); }
        vm.PropertyChanged += Loaded; var startedQpc = Stopwatch.GetTimestamp(); var clock = Stopwatch.StartNew();
        try
        {
            trace.SetPhase(phase + ".read"); trace.Measure("Select diff file", () => vm.SelectedChange = target);
            if (!vm.IsDiffLoading) completion.TrySetResult();
            await completion.Task.WaitAsync(ct); trace.SetPhase(phase + ".data-ready-await-bindings");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
            var editor = window.FindControl<DiffEditor>("DiffViewer")!;
            if (vm.IsLargeDiff || !vm.Diff.Contains("changed line", StringComparison.Ordinal) || editor.Source != vm.Diff ||
                !editor.GetVisualDescendants().OfType<CodeEditor>().Any(e => e.IsEffectivelyVisible && e.CodeText == vm.Diff))
                throw new InvalidOperationException("The generated file diff did not reach the visible inline editor.");
            trace.SetPhase(phase + ".layout"); trace.Measure("Window.UpdateLayout", window.UpdateLayout);
            trace.SetPhase(phase + ".await-native-frame"); await FrameAsync(window, ct); var frame = clock.Elapsed.TotalMilliseconds;
            trace.SetPhase(phase + ".await-surface-submission"); var surface = await SurfaceSubmissionAsync(window, startedQpc, ct);
            trace.SetPhase(phase + ".complete"); return new(frame, Stopwatch.GetElapsedTime(startedQpc, surface.CompletedQpc).TotalMilliseconds, surface);
        }
        finally { vm.PropertyChanged -= Loaded; }
    }

    private static async Task<JsonElement> ValidateFixtureAsync(string root, GitProcessRunner runner, string version, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(Path.Combine(root, ".git")) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Probe requires a standalone generated fixture, without a redirected root.");
        var manifestPath = root + ".fixture.json";
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 65_536) throw new InvalidOperationException("Generated fixture manifest missing or invalid.");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct)); var manifest = json.RootElement;
        if (manifest.GetProperty("git").GetString() != version) throw new InvalidOperationException("Probe Git version differs from fixture manifest: " + version);
        if (manifest.GetProperty("schema").GetInt32() != 1 || !Path.GetFullPath(manifest.GetProperty("root").GetString()!).Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            manifest.GetProperty("commits").GetInt32() != 100_000 || manifest.GetProperty("files").GetInt32() != 50_000 || !Guid.TryParseExact(manifest.GetProperty("fingerprint").GetString(), "N", out _))
            throw new InvalidOperationException("Probe requires the generated 100000-commit / 50000-file fixture manifest.");
        async Task<byte[]> Git(params string[] args)
        {
            var result = await runner.RunAsync(root, args, ct, new() { HideOutput = true });
            if (result.ExitCode != 0) throw new InvalidOperationException("Fixture validation failed: " + result.Error); return result.Output;
        }
        var config = Encoding.UTF8.GetString(await Git("config", "--local", "--null", "--list"));
        foreach (var item in config.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('\n', 2); var name = pair[0]; var value = pair.ElementAtOrDefault(1) ?? "";
            if (name.StartsWith("include", StringComparison.OrdinalIgnoreCase) || name.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("diff.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("credential.", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("core.fsmonitor", StringComparison.OrdinalIgnoreCase) && value is not ("true" or "false") ||
                name.Equals("core.hooksPath", StringComparison.OrdinalIgnoreCase) && !Path.GetFullPath(value, root).Equals(Path.Combine(root, ".git", "hooks"), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture-local executable configuration is not permitted for unattended probes.");
        }
        if (File.Exists(Path.Combine(root, ".git", "config.worktree"))) throw new InvalidOperationException("Fixture worktree configuration is not permitted.");
        var hooks = Path.Combine(root, ".git", "hooks");
        if (Directory.Exists(hooks) && Directory.EnumerateFiles(hooks).Any(p => !p.EndsWith(".sample", StringComparison.Ordinal))) throw new InvalidOperationException("Fixture hooks are not permitted.");
        if (Encoding.UTF8.GetString(await Git("rev-parse", "HEAD")).Trim() != manifest.GetProperty("head").GetString() ||
            Encoding.UTF8.GetString(await Git("rev-list", "--count", "HEAD")).Trim() != "100000" ||
            (await Git("ls-files", "-z")).Count(b => b == 0) != 50_000)
            throw new InvalidOperationException("Actual Git HEAD/commit/index shape differs from generated manifest.");
        return manifest.Clone();
    }

    private static Task FrameAsync(Window window, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => completion.TrySetCanceled(ct));
        window.RequestAnimationFrame(_ => { registration.Dispose(); completion.TrySetResult(); }); window.InvalidateVisual(); return completion.Task;
    }
    private async Task<SurfaceSubmission> SurfaceSubmissionAsync(Window window, long startedQpc, CancellationToken ct)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
            window.UpdateLayout();
            var compositor = ElementComposition.GetElementVisual(window)?.Compositor
                ?? throw new InvalidOperationException("Native window compositor unavailable.");
            var requested = Stopwatch.GetTimestamp();
            // A visible text control may legitimately have an empty draw list. The
            // opt-in probe supplies exactly one opaque device pixel to dirty this target.
            // Its lease retains existing composition content and restores it in finally.
            using var ink = new SurfaceInkMarker(window);
            var scene = new TaskCompletionSource<(long UpdatedQpc, Task<long> Completion)>(TaskCreationOptions.RunContinuationsAsynchronously);
            // RecalculateChildren queues CompositingRenderer.Update first. This public callback
            // then runs in CommitCore, before serialization of that same batch.
            compositor.RequestCompositionUpdate(() =>
            {
                var updated = Stopwatch.GetTimestamp();
                var batch = compositor.RequestCompositionBatchCommitAsync();
                // Attach before this batch can be serialized/rendered, so even a fast
                // render records its completion on the render thread, not a later UI continuation.
                var completion = batch.Rendered.ContinueWith(task =>
                {
                    task.GetAwaiter().GetResult(); return Stopwatch.GetTimestamp();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                scene.TrySetResult((updated, completion));
            });
            var prepared = await scene.Task.WaitAsync(ct);
            var completed = await prepared.Completion.WaitAsync(ct);
            var inkRenderedQpc = ink.RequireRendered(prepared.UpdatedQpc, completed);
            var render = nativeMetrics.Snapshot().LastOrDefault(sample => sample.Instrument == ScrollRenderEvidence.RenderInstrument && sample.RecordedQpc >= inkRenderedQpc && sample.RecordedQpc <= completed)
                ?? throw new InvalidOperationException($"Rendered batch has no matching real render histogram (request={requested}, scene={prepared.UpdatedQpc}, completed={completed}).");
            if (render.Unit != "ms" || !double.IsFinite(render.Value) || render.Value < 0)
                throw new InvalidOperationException("Invalid rendered batch histogram.");
            // Rendered completes from finally. The enclosing error observer separately
            // invalidates measurements on any render/session-disposal failure.
            return new(startedQpc, requested, prepared.UpdatedQpc, completed, render.RecordedQpc, render.Value, true, ink.RepaintTargetType)
            {
                ProbeInkMarker = new { ink.Bounds.X, ink.Bounds.Y, ink.Bounds.Width, ink.Bounds.Height,
                    devicePixels = 1, renderedQpc = inkRenderedQpc,
                    purpose = "Probe-only opaque composition ink in the product scene batch; detached and previous child restored after the batch completes." }
            };
        }
        catch
        {
            surfaceBarrierWindowFailures.Enqueue(new
            {
                recordedQpc = Stopwatch.GetTimestamp(), window.IsActive, window.IsVisible, window.IsEffectivelyVisible,
                state = window.WindowState.ToString(), window.Opacity, window.RenderScaling,
                bounds = new { window.Bounds.X, window.Bounds.Y, window.Bounds.Width, window.Bounds.Height },
                clientSize = new { window.ClientSize.Width, window.ClientSize.Height },
                boundary = "Own probe window state only; IsActive is not proof of occlusion or physical display."
            });
            throw;
        }
    }
    private sealed record FrameSampling(object Report, long StartQpc, long EndQpc);
    private static async Task<FrameSampling> SampleFramesAsync(Window window, Action scroll, TimeSpan duration, CancellationToken ct)
    {
        var intervals = new List<double>(); var callbackDurations = new List<double>(); var clock = Stopwatch.StartNew(); var previous = clock.Elapsed.TotalMilliseconds;
        var startQpc = Stopwatch.GetTimestamp();
        while (clock.Elapsed < duration)
        {
            var started = Stopwatch.GetTimestamp(); scroll(); callbackDurations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await FrameAsync(window, ct); var now = clock.Elapsed.TotalMilliseconds; intervals.Add(now - previous); previous = now;
        }
        var endQpc = Stopwatch.GetTimestamp();
        return new(new { samples = intervals.Count, elapsedMs = clock.Elapsed.TotalMilliseconds, callbackFps = intervals.Count / clock.Elapsed.TotalSeconds,
            intervals = Statistics(intervals), intervalsInOrder = intervals, scrollProviderCallbackDurationMs = Statistics(callbackDurations),
            startQpc, endQpc, timestampFrequency = Stopwatch.Frequency }, startQpc, endQpc);
    }

    private sealed record MemorySample(long RecordedQpc, long WorkingSetBytes);
    private async Task<(object Callbacks, object Rendering, object Memory)> SampleScrollFramesAsync(Window window, Action scroll, ProbeTrace trace, CancellationToken ct)
    {
        var platform = window.PlatformImpl ?? throw new InvalidOperationException("Native window platform unavailable.");
        var compositor = ElementComposition.GetElementVisual(window)?.Compositor
            ?? throw new InvalidOperationException("Native window compositor unavailable.");
        // Avalonia reference assemblies intentionally hide these platform internals.
        // Diagnostic inspection is read-only and pinned; absence remains explicit.
        const BindingFlags platformFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var surfaceTypes = (platform.GetType().GetProperty("Surfaces", platformFlags)?.GetValue(platform) as IEnumerable<object>)?
            .Select(s => s.GetType().FullName).ToArray() ?? [];
        string? graphicsType = null;
        var completions = new ConcurrentQueue<RenderBatchCompletion>();
        var observed = new HashSet<CompositionBatch>();
        var pending = new List<Task>();
        long batchId = 0; var inspectContext = 1;
        using var errors = new RenderErrorObserver();
        using var memoryCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var memory = new ConcurrentQueue<MemorySample>();
        var memoryReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var memoryWorker = Task.Run(async () =>
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                void Sample() { process.Refresh(); var bytes = process.WorkingSet64; memory.Enqueue(new(Stopwatch.GetTimestamp(), bytes)); }
                Sample(); memoryReady.TrySetResult();
                while (!memoryCancellation.IsCancellationRequested)
                {
                    try { await Task.Delay(100, memoryCancellation.Token); }
                    catch (OperationCanceledException) when (memoryCancellation.IsCancellationRequested) { break; }
                    Sample();
                }
                Sample();
            }
            catch (Exception exception) { memoryReady.TrySetException(exception); throw; }
        }, CancellationToken.None);
        void Observe(CompositionBatch batch)
        {
            if (!observed.Add(batch)) return;
            var id = ++batchId; var requested = Stopwatch.GetTimestamp();
            pending.Add(batch.Rendered.ContinueWith(completed =>
            {
                var finished = Stopwatch.GetTimestamp();
                string? context = null, inspectionError = null;
                if (Interlocked.Exchange(ref inspectContext, 0) == 1)
                {
                    try
                    {
                        // Probe-only read on the render thread; never calls Value/EnsureCurrent or creates a context.
                        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        var server = compositor.GetType().GetProperty("Server", flags)?.GetValue(compositor)
                            ?? throw new MissingMemberException("Compositor.Server");
                        var renderInterface = server.GetType().GetProperty("RenderInterface", flags)?.GetValue(server)
                            ?? throw new MissingMemberException("Server.RenderInterface");
                        var gpuProperty = renderInterface.GetType().GetProperty("GpuContext", flags)
                            ?? throw new MissingMemberException("RenderInterface.GpuContext");
                        graphicsType = renderInterface.GetType().GetField("_graphics", flags)?.GetValue(renderInterface)?.GetType().FullName;
                        context = gpuProperty.GetValue(renderInterface)?.GetType().FullName ?? "Software (no GPU context)";
                    }
                    catch (Exception e) { inspectionError = e.GetType().Name + ": " + e.Message; }
                }
                completions.Enqueue(new(id, requested, finished, completed.IsCompletedSuccessfully, trace.CurrentPhase, context, inspectionError));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        }
        FrameSampling sampled;
        try
        {
            await memoryReady.Task.WaitAsync(ct);
            trace.SetPhase("graph-scroll");
            sampled = await SampleFramesAsync(window, () =>
            {
                scroll(); Observe(compositor.RequestCompositionBatchCommitAsync());
            }, TimeSpan.FromSeconds(5), ct);
        }
        finally { memoryCancellation.Cancel(); await memoryWorker; }
        trace.SetPhase("graph-scroll.draining");
        Observe(compositor.RequestCompositionBatchCommitAsync());
        await Task.WhenAll(pending).WaitAsync(ct);
        var drainedQpc = Stopwatch.GetTimestamp();
        var errorSnapshot = errors.Snapshot();
        if (!errors.IsStillInstalled) errorSnapshot = [.. errorSnapshot, new(drainedQpc, "Avalonia error observer was replaced during scroll.")];
        var completedSnapshot = completions.ToArray();
        var memorySnapshot = memory.ToArray();
        // The measured scroll and batch drain have ended. Analyze immutable evidence on a worker,
        // retaining the draining phase so unrelated UI work remains covered by the external profiler.
        var analysis = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var batches = completedSnapshot.OrderBy(c => c.CompletedQpc).ToArray();
            var report = ScrollRenderEvidence.Evaluate(sampled.StartQpc, sampled.EndQpc, Stopwatch.Frequency,
                nativeMetrics.Snapshot(), batches, errorSnapshot.Select(e => e.Message));
            ct.ThrowIfCancellationRequested();
            return (Report: report, Batches: batches, MaximumWorkingSetBytes: memorySnapshot.Max(sample => sample.WorkingSetBytes));
        }, ct);
        trace.SetPhase("graph-scroll.complete");
        return (sampled.Report, new
        {
            evidence = analysis.Report, platformType = platform.GetType().FullName, selectedSurfaceTypes = surfaceTypes,
            platformGraphicsType = graphicsType, batchCompletionsInOrder = analysis.Batches, drainedQpc,
            avaloniaErrors = errorSnapshot,
            activeGpuContextInspection = "Read-only, probe-only reflection of pinned Avalonia11.3.22 platform Surfaces and Server.RenderInterface.GpuContext; context is read on synchronous render-thread batch completion. Reference assemblies hide these internals, so unavailable members are explicitly reported. Compositor/batch lifetime is obtained through public ElementComposition APIs.",
            successBoundary = "Rendered tasks are completed from finally; their success alone does not prove successful rendering. Gate additionally requires genuine render histogram scopes and no observed Avalonia errors. Surface-update return does not prove GPU execution or Windows compositor/display completion."
        }, new { intervalMs = 100, startQpc = sampled.StartQpc, endQpc = sampled.EndQpc, timestampFrequency = Stopwatch.Frequency,
            samplesInOrder = memorySnapshot, maximumWorkingSetBytes = analysis.MaximumWorkingSetBytes });
    }

    private sealed record RenderError(long RecordedQpc, string Message);
    private sealed class RenderErrorObserver : ILogSink, IDisposable
    {
        private readonly ILogSink? previous = Logger.Sink;
        private readonly ConcurrentQueue<RenderError> errors = new();
        public RenderErrorObserver() => Logger.Sink = this;
        public bool IsStillInstalled => ReferenceEquals(Logger.Sink, this);
        public RenderError[] Snapshot() => errors.ToArray();
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Error || previous?.IsEnabled(level, area) == true;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            Record(level, area, messageTemplate); previous?.Log(level, area, source, messageTemplate);
        }
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            Record(level, area, messageTemplate); previous?.Log(level, area, source, messageTemplate, propertyValues);
        }
        private void Record(LogEventLevel level, string area, string template)
        {
            if (level >= LogEventLevel.Error) errors.Enqueue(new(Stopwatch.GetTimestamp(), area + ": " + template));
        }
        public void Dispose() { if (IsStillInstalled) Logger.Sink = previous; }
    }
    private sealed record HeartbeatSample(double TimerElapsedMs, double PostedMs, double DeliveredMs, double QueueDelayMs,
        string PostedPhase, string DeliveredPhase, double GcPauseDeltaMs, int Gen0Delta, int Gen1Delta, int Gen2Delta,
        int WorkerThreadsAtPost, long PendingWorkAtPost, double? WorkerGcPauseDeltaMs,
        int? WorkerGen0Delta, int? WorkerGen1Delta, int? WorkerGen2Delta);
    private static async Task HeartbeatAsync(List<HeartbeatSample> samples, ProbeTrace trace, CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            double? previousWorkerPause = null;
            var previousGen0 = 0; var previousGen1 = 0; var previousGen2 = 0;
            while (!ct.IsCancellationRequested)
            {
                var timerStarted = trace.ElapsedMs; await Task.Delay(16, ct).ConfigureAwait(false);
                var posted = trace.ElapsedMs; var phase = trace.CurrentPhase; var pause = GC.GetTotalPauseDuration().TotalMilliseconds;
                var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
                var workers = ThreadPool.ThreadCount; var pending = ThreadPool.PendingWorkItemCount;
                var hasPreviousPost = previousWorkerPause.HasValue;
                double? workerPause = hasPreviousPost ? pause - previousWorkerPause!.Value : null;
                int? workerGen0 = hasPreviousPost ? gen0 - previousGen0 : null;
                int? workerGen1 = hasPreviousPost ? gen1 - previousGen1 : null;
                int? workerGen2 = hasPreviousPost ? gen2 - previousGen2 : null;
                previousWorkerPause = pause; previousGen0 = gen0; previousGen1 = gen1; previousGen2 = gen2;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var delivered = trace.ElapsedMs;
                    samples.Add(new(posted - timerStarted, posted, delivered, delivered - posted, phase, trace.CurrentPhase,
                        GC.GetTotalPauseDuration().TotalMilliseconds - pause, GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1,
                        GC.CollectionCount(2) - gen2, workers, pending, workerPause, workerGen0, workerGen1, workerGen2));
                }, DispatcherPriority.Normal, ct);
            }
        }, ct);
    }

    private sealed class ProbeTrace : IDisposable
    {
        private readonly MainWindowViewModel vm;
        public long OriginQpc { get; } = Stopwatch.GetTimestamp();
        private readonly NativePerformanceMetrics metrics;
        private string phase = "startup";
        private readonly Dictionary<string, (double Start, double Pause, string Phase)> setters = [];
        public DateTimeOffset OriginUtc { get; } = DateTimeOffset.UtcNow;
        public double ElapsedMs => Stopwatch.GetElapsedTime(OriginQpc).TotalMilliseconds;
        public string CurrentPhase => Volatile.Read(ref phase);
        public List<object> Phases { get; } = [];
        public List<object> Spans { get; } = [];
        public ProbeTrace(MainWindowViewModel vm, NativePerformanceMetrics metrics) { this.vm = vm; this.metrics = metrics; vm.PropertyChanging += Changing; vm.PropertyChanged += Changed; }
        public void SetPhase(string value) { Volatile.Write(ref phase, value); metrics.SetPhase(value); Phases.Add(new { phase = value, elapsedMs = ElapsedMs }); }
        public void Measure(string name, Action action)
        {
            var start = ElapsedMs; var pause = GC.GetTotalPauseDuration().TotalMilliseconds;
            try { action(); }
            finally { AddSpan("explicit synchronous call", name, CurrentPhase, start, pause, null); }
        }
        private void Changing(object? sender, PropertyChangingEventArgs e)
        {
            if (e.PropertyName is nameof(vm.Commands) or nameof(vm.Diff) or nameof(vm.LeftDiff) or nameof(vm.RightDiff))
                setters[e.PropertyName] = (ElapsedMs, GC.GetTotalPauseDuration().TotalMilliseconds, CurrentPhase);
        }
        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not { } name || !setters.Remove(name, out var started)) return;
            var length = name switch { nameof(vm.Commands) => vm.Commands.Length, nameof(vm.Diff) => vm.Diff.Length,
                nameof(vm.LeftDiff) => vm.LeftDiff.Length, nameof(vm.RightDiff) => vm.RightDiff.Length, _ => 0 };
            AddSpan("bound property setter and earlier observers", name, started.Phase, started.Start, started.Pause, length);
        }
        private void AddSpan(string kind, string name, string spanPhase, double start, double pause, int? length)
        {
            var end = ElapsedMs;
            Spans.Add(new { kind, name, phase = spanPhase, startedMs = start, completedMs = end, durationMs = end - start,
                gcPauseDeltaMs = GC.GetTotalPauseDuration().TotalMilliseconds - pause, valueLength = length });
        }
        public void Dispose() { vm.PropertyChanging -= Changing; vm.PropertyChanged -= Changed; }
    }
    private static object Statistics(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToArray();
        return new { samples = sorted.Length, min = sorted.FirstOrDefault(), median = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2],
            p95 = sorted.Length == 0 ? 0 : sorted[(int)Math.Floor((sorted.Length - 1) * .95)], max = sorted.LastOrDefault(), values = sorted };
    }
}
