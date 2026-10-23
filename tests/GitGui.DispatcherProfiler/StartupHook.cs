using System.Diagnostics;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using HarmonyLib;

internal static class StartupHook
{
    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This external profiler targets the Windows shipping desktop.");
        // The launcher sets this for one diagnostic child only. Git/askpass children must not inherit injection.
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
        var output = Environment.GetEnvironmentVariable("GITGUI_DISPATCHER_DIAGNOSTIC");
        if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("Diagnostic output must be explicitly specified.");
        Environment.SetEnvironmentVariable("GITGUI_DISPATCHER_DIAGNOSTIC", null);
        var directory = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
        AssemblyLoadContext.Default.Resolving += (_, name) => name.Name == "0Harmony"
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "0Harmony.dll")) : null;
        Install(output);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Install(string output) => Profiler.Install(output);
}

internal static class Profiler
{
    private const int Capacity = 262144;
    private static readonly SpanRecord[] Records = new SpanRecord[Capacity];
    private static readonly List<object> Patches = [];
    private static readonly uint UiTid = GetCurrentThreadId();
    private static readonly long OriginQpc = Stopwatch.GetTimestamp();
    private static readonly FieldInfo CallbackField = typeof(DispatcherOperation).GetField("Callback", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static string phase = "application";
    private static int count, overflow, canaryStarted;
    private static string? failure;
    private static bool queuedExceptionPreserved, sendExceptionPreserved, canaryFinished;
    private static ActivityListener? layoutListener;
    [ThreadStatic] private static int parent;

    public static void Install(string output)
    {
        output = Path.GetFullPath(output);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Diagnostic output already exists.");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteReport(output);
        var harmony = new Harmony("gitgui.external.dispatcher.diagnostic");
        Patch(harmony, typeof(DispatcherOperation).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic)!, typeof(ExecutePatch));
        Patch(harmony, typeof(Dispatcher).GetMethod("Invoke", [typeof(Action), typeof(DispatcherPriority), typeof(CancellationToken), typeof(TimeSpan)])!, typeof(SendPatch));
        var win32 = Assembly.Load("Avalonia.Win32");
        var native = win32.GetType("Avalonia.Win32.WindowImpl", throwOnError: true)!;
        Patch(harmony, native.GetMethod("AppWndProc", BindingFlags.Instance | BindingFlags.NonPublic)!, typeof(WndProcPatch));
        if (Environment.GetEnvironmentVariable("GITGUI_PROCESS_LIFECYCLE") == "1")
            ProcessLifecycle.Install((method, patch) => Patch(harmony, method, patch));
        Environment.SetEnvironmentVariable("GITGUI_PROCESS_LIFECYCLE", null);
        if (Environment.GetEnvironmentVariable("GITGUI_LAYOUT_ACTIVITIES") == "1") EnableLayoutActivities();
        Environment.SetEnvironmentVariable("GITGUI_LAYOUT_ACTIVITIES", null);
    }
    private static void EnableLayoutActivities()
    {
        layoutListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Avalonia.Diagnostic.Source",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Name is "Avalonia.MeasuringLayoutable" or "Avalonia.ArrangingLayoutable"
                ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStarted = activity => activity.SetCustomProperty("gitgui.span", Enter(Stopwatch.GetTimestamp(), activity.OperationName)),
            ActivityStopped = activity =>
            {
                if (activity.GetCustomProperty("gitgui.span") is not SpanState state) return;
                foreach (var tag in activity.TagObjects)
                    if (tag.Value is Avalonia.Layout.Layoutable element && state.Id > 0)
                    {
                        Records[state.Id - 1].Callback = element.GetType().FullName +
                            (element is Control { Name: { Length: > 0 } name } ? "#" + name : "");
                        break;
                    }
                Leave(state, null);
            }
        };
        ActivitySource.AddActivityListener(layoutListener);
    }
    private static void Patch(Harmony harmony, MethodInfo method, Type patch)
    {
        if (method is null || method.GetMethodBody() is null) throw new InvalidOperationException("Expected shipping method with IL not found.");
        using var image = new PEReader(File.OpenRead(method.Module.FullyQualifiedName));
        Patches.Add(new { method = method.DeclaringType!.FullName + "." + method.Name, token = method.MetadataToken,
            mvid = method.Module.ModuleVersionId, module = method.Module.FullyQualifiedName,
            managedNativeHeaderSize = image.PEHeaders.CorHeader?.ManagedNativeHeaderDirectory.Size ?? 0,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(method.Module.FullyQualifiedName))) });
        harmony.Patch(method, new HarmonyMethod(patch.GetMethod("Prefix")!), finalizer: new HarmonyMethod(patch.GetMethod("Finalizer")!));
    }
    public static SpanState Enter(long started, string seam, string? callback = null)
    {
        var id = Interlocked.Increment(ref count);
        if (id > Capacity) { Interlocked.Increment(ref overflow); return default; }
        var state = new SpanState(id, parent);
        Records[id - 1] = new() { Id = id, Parent = parent, Seam = seam, Callback = callback,
            EnterQpc = started, NativeTid = GetCurrentThreadId(), Phase = Volatile.Read(ref phase) };
        parent = id;
        return state;
    }
    public static void Leave(SpanState state, Exception? exception)
    {
        if (state.Id == 0) return;
        ref var record = ref Records[state.Id - 1];
        record.ExitQpc = Stopwatch.GetTimestamp(); record.ExitPhase = Volatile.Read(ref phase);
        record.ExceptionType = exception?.GetType().FullName; record.Completed = true;
        parent = state.Parent;
    }
    public static string? Callback(object operation) => (CallbackField.GetValue(operation) as Delegate)?.Method is { } method
        ? method.DeclaringType?.FullName + "." + method.Name : null;
    public static void MaybeStartCanary(uint message)
    {
        if (message != 0x000F || Environment.GetEnvironmentVariable("GITGUI_DISPATCHER_CANARY") != "1" ||
            Interlocked.Exchange(ref canaryStarted, 1) != 0) return;
        Dispatcher.UIThread.Post(RunCanary);
    }
    private static async void RunCanary()
    {
        try
        {
            phase = "canary.queued40"; await Dispatcher.UIThread.InvokeAsync(CanaryQueued);
            phase = "canary.send40"; Dispatcher.UIThread.Invoke(CanarySend, DispatcherPriority.Send, CancellationToken.None, Timeout.InfiniteTimeSpan);
            phase = "canary.queued-exception";
            var queued = new CanaryException();
            try { await Dispatcher.UIThread.InvokeAsync(() => throw queued); }
            catch (CanaryException caught) { queuedExceptionPreserved = ReferenceEquals(caught, queued); }
            phase = "canary.send-exception";
            var send = new CanaryException();
            try { Dispatcher.UIThread.Invoke(() => throw send, DispatcherPriority.Send, CancellationToken.None, Timeout.InfiniteTimeSpan); }
            catch (CanaryException caught) { sendExceptionPreserved = ReferenceEquals(caught, send); }
            phase = "canary.wndproc40";
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
            var window = lifetime.MainWindow!;
            var property = typeof(Window).GetProperty("PlatformImpl", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            var impl = property.GetValue(window)!;
            var activated = impl.GetType().GetProperty("Activated")!;
            var previous = (Action?)activated.GetValue(impl);
            activated.SetValue(impl, (Action)(() => { Thread.Sleep(40); previous?.Invoke(); }));
            try { SendMessage(window.TryGetPlatformHandle()!.Handle, 0x0006, new IntPtr(1), IntPtr.Zero); }
            finally { activated.SetValue(impl, previous); }
            phase = "application"; canaryFinished = true;
        }
        catch (Exception exception) { failure = exception.ToString(); phase = "canary.failed"; }
    }
    private static void CanaryQueued() => Thread.Sleep(40);
    private static void CanarySend() => Thread.Sleep(40);
    private static void WriteReport(string output)
    {
        layoutListener?.Dispose();
        var captured = Records.Take(Math.Min(Volatile.Read(ref count), Capacity)).ToArray();
        var probeArguments = Environment.GetCommandLineArgs();
        var reportArgument = Array.IndexOf(probeArguments, "--performance-report");
        var hasPhases = false;
        string? desktopProbeReportPath = null, desktopProbeReportSha256 = null;
        if (reportArgument >= 0 && reportArgument + 1 < probeArguments.Length && File.Exists(probeArguments[reportArgument + 1]))
        {
            desktopProbeReportPath = Path.GetFullPath(probeArguments[reportArgument + 1]);
            var probeBytes = File.ReadAllBytes(desktopProbeReportPath);
            desktopProbeReportSha256 = Convert.ToHexString(SHA256.HashData(probeBytes));
            using var probe = JsonDocument.Parse(probeBytes);
            if (probe.RootElement.TryGetProperty("timelineOriginQpc", out var origin))
            {
                var phases = probe.RootElement.GetProperty("phaseTimeline").EnumerateArray()
                    .Select(p => (At: origin.GetInt64() + (long)(p.GetProperty("elapsedMs").GetDouble() * Stopwatch.Frequency / 1000),
                        Phase: p.GetProperty("phase").GetString())).OrderBy(p => p.At).ToArray();
                string? PhaseAt(long timestamp) => phases.LastOrDefault(p => p.At <= timestamp).Phase;
                for (var i = 0; i < captured.Length; i++)
                { captured[i].ProductPhaseAtEnter = PhaseAt(captured[i].EnterQpc); captured[i].ProductPhaseAtExit = PhaseAt(captured[i].ExitQpc); }
                hasPhases = true;
            }
        }
        var blocking = captured.Where(r => r.NativeTid == UiTid && r.Completed && Stopwatch.GetElapsedTime(r.EnterQpc, r.ExitQpc).TotalMilliseconds > 16).ToArray();
        var report = new
        {
            schema = 1, runtime = RuntimeInformation.FrameworkDescription, originQpc = OriginQpc, timestampFrequency = Stopwatch.Frequency,
            targetProcessId = Environment.ProcessId,
            targetExecutableSha256 = HashFile(Environment.ProcessPath),
            targetAppAssemblySha256 = HashFile(Assembly.GetEntryAssembly()?.Location),
            hookAssemblySha256 = HashFile(typeof(StartupHook).Assembly.Location),
            desktopProbeReportPath, desktopProbeReportSha256,
            harmony = new { version = typeof(Harmony).Assembly.GetName().Version?.ToString(),
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Harmony).Assembly.Location))) },
            uiNativeTid = UiTid, totalEntered = count, overflow, patches = Patches, productPhasesCorrelated = hasPhases,
            layoutActivitiesEnabled = layoutListener is not null,
            processLifecycle = ProcessLifecycle.Finish(),
            coverage = "All DispatcherOperation.Execute entries; UI Send fastpath final Action Invoke overload; WindowImpl.AppWndProc. Nested spans inclusive. Optional layout ActivityListener records control type/fixed Name, never content; its per-control Activity allocations can substantially inflate layout time. Does not cover generic Send Invoke<T>, other native windows/COM callbacks, idle waits or actual compositor presentation. Shipping methods are dynamically replaced by Harmony and execute instrumented generated code; startup/working-set measurements from this run are diagnostic, not product baseline.",
            canary = new { started = canaryStarted != 0, finished = canaryFinished, queuedExceptionPreserved, sendExceptionPreserved, failure },
            gate = new { targetMs = 16, accepted = overflow == 0 && captured.All(r => r.Completed) && blocking.Length == 0,
                exceedingCount = blocking.Length, allSpansComplete = captured.All(r => r.Completed) },
            spans = captured
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var reportStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(reportStream, report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
    }
    private static string? HashFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream));
    }
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private sealed class CanaryException : Exception { }
}

internal readonly record struct SpanState(int Id, int Parent);
internal struct SpanRecord
{
    public int Id, Parent;
    public string? Seam, Callback, Phase, ExitPhase, ExceptionType, ProductPhaseAtEnter, ProductPhaseAtExit;
    public long EnterQpc, ExitQpc;
    public uint NativeTid;
    public bool Completed;
}
internal static class ExecutePatch
{
    public static void Prefix(object __instance, out SpanState __state)
    { var started = Stopwatch.GetTimestamp(); __state = Profiler.Enter(started, "DispatcherOperation.Execute", Profiler.Callback(__instance)); }
    public static void Finalizer(SpanState __state, Exception? __exception) => Profiler.Leave(__state, __exception);
}
internal static class SendPatch
{
    public static void Prefix(Dispatcher __instance, Action callback, DispatcherPriority priority, CancellationToken cancellationToken, out SpanState __state)
    {
        var started = Stopwatch.GetTimestamp();
        __state = !cancellationToken.IsCancellationRequested && priority == DispatcherPriority.Send && __instance.CheckAccess()
            ? Profiler.Enter(started, "Dispatcher.Invoke UI Send", callback.Method.DeclaringType?.FullName + "." + callback.Method.Name) : default;
    }
    public static void Finalizer(SpanState __state, Exception? __exception) => Profiler.Leave(__state, __exception);
}
internal static class WndProcPatch
{
    public static void Prefix(out SpanState __state) => __state = Profiler.Enter(Stopwatch.GetTimestamp(), "WindowImpl.AppWndProc");
    public static void Finalizer(SpanState __state, Exception? __exception, uint msg)
    {
        Profiler.Leave(__state, __exception);
        if (__exception is null) Profiler.MaybeStartCanary(msg);
    }
}
