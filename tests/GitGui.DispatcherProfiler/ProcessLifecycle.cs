using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Microsoft.Win32.SafeHandles;

internal static class ProcessLifecycle
{
    private static readonly AsyncLocal<long> Scope = new();
    private static readonly object Gate = new();
    private static readonly List<Child> Children = [];
    private static readonly List<Notification> Notifications = [];
    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal)
    { "config", "status", "diff", "diff-files", "diff-index", "log", "cat-file", "rev-parse", "rev-list", "ls-files", "for-each-ref", "version", "--version" };
    private static long scopes;
    private static int failures;
    private static bool stopping;
    public static bool Enabled { get; private set; }

    public static void Install(Action<MethodInfo, Type> patch)
    {
        Enabled = true;
        var runner = Assembly.Load("GitGui.Engine.Cli").GetType("GitGui.Engine.Cli.GitProcessRunner", throwOnError: true)!;
        patch(runner.GetMethod("RunCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!, typeof(RunScopePatch));
        patch(runner.GetMethod("Notify", BindingFlags.NonPublic | BindingFlags.Instance)!, typeof(NotifyPatch));
        patch(typeof(Process).GetMethod("Start", BindingFlags.Public | BindingFlags.Instance, [])!, typeof(ProcessStartPatch));
    }
    public static long PushScope()
    { var previous = Scope.Value; Scope.Value = Interlocked.Increment(ref scopes); return previous; }
    public static void PopScope(long previous) => Scope.Value = previous;
    public static StartState? BeforeStart(Process process)
    {
        try
        {
            var start = process.StartInfo;
            if (!Path.GetFileNameWithoutExtension(start.FileName).Equals("git", StringComparison.OrdinalIgnoreCase)) return null;
            var qpc = Stopwatch.GetTimestamp(); var utc = DateTimeOffset.UtcNow;
            return new(Scope.Value, Classify(start.ArgumentList), qpc, utc, Stopwatch.GetTimestamp());
        }
        catch (Exception) { Interlocked.Increment(ref failures); return null; }
    }
    private static string Classify(IEnumerable<string> arguments)
    {
        var skip = false;
        foreach (var argument in arguments)
        {
            if (skip) { skip = false; continue; }
            if (argument is "-c" or "-C" or "--git-dir" or "--work-tree") { skip = true; continue; }
            if (Verbs.Contains(argument)) return argument == "--version" ? "version" : argument;
            if (!argument.StartsWith('-')) return "other";
        }
        return "other";
    }
    public static void AfterStart(Process process, StartState? state, bool started, Exception? exception)
    {
        if (state is null) return;
        IntPtr duplicate = IntPtr.Zero;
        try
        {
            if (!started || exception is not null) { Interlocked.Increment(ref failures); return; }
            var returned = Stopwatch.GetTimestamp();
            if (!DuplicateHandle(GetCurrentProcess(), process.SafeHandle, GetCurrentProcess(), out duplicate, 0, false, 2))
                throw new InvalidOperationException("Unable to duplicate own-child process handle.");
            var child = new Child(process.Id, state, returned, new ProcessWaitHandle(duplicate)); duplicate = IntPtr.Zero;
            lock (Gate)
            {
                if (stopping) { child.Dispose(); return; }
                Children.Add(child);
            }
            child.Observe();
        }
        catch (Exception) { Interlocked.Increment(ref failures); }
        finally { if (duplicate != IntPtr.Zero) CloseHandle(duplicate); }
    }
    public static void Notify(object entry)
    {
        try
        {
            var type = entry.GetType();
            var arguments = type.GetProperty("Arguments")!.GetValue(entry) as IEnumerable<string> ?? [];
            var duration = (TimeSpan)type.GetProperty("Duration")!.GetValue(entry)!;
            var exit = (int)type.GetProperty("ExitCode")!.GetValue(entry)!;
            var notification = new Notification(Scope.Value, Classify(arguments), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, duration.TotalMilliseconds, exit);
            lock (Gate) { if (!stopping) Notifications.Add(notification); }
        }
        catch (Exception) { Interlocked.Increment(ref failures); }
    }
    public static object Finish()
    {
        lock (Gate)
        {
            stopping = true;
            var children = Children.Select(child => child.Finish()).ToArray();
            return new { enabled = Enabled, observationFailures = failures,
                boundary = "Only Git processes started directly by this app. Duplicate own-child handles observe kernel GetProcessTimes creation/exit FILETIME; exit times are read only once the handle is signaled. ExitObserverQpc is a separately scheduled callback, not kernel exit time. Notify uses external AsyncLocal operation IDs; no task/stream/event behavior is modified. A cmd/git shim lifetime includes its child wait. UTC/QPC clock pairs are retained; clock conversion precision is not assumed. Duplicates/wait registrations are released before this report is serialized. No argv/path/environment/stdout/stderr content is recorded.",
                children, notifications = Notifications.ToArray() };
        }
    }
    internal sealed record StartState(long OperationId, string CommandClass, long StartCallQpc, DateTimeOffset StartCallUtc, long ClockPairAfterQpc);
    private sealed record Notification(long OperationId, string CommandClass, long NotifyQpc, DateTimeOffset NotifyUtc, double RunnerDurationMs, int ExitCode);
    private sealed class ProcessWaitHandle : WaitHandle
    { public ProcessWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true); }
    private sealed class Child(int pid, StartState state, long returnedQpc, ProcessWaitHandle handle) : IDisposable
    {
        private readonly object childGate = new();
        private RegisteredWaitHandle? registration;
        private long? observerQpc, creation, exited, kernel, user;
        private uint? exitCode;
        private bool disposed;
        public void Observe()
        {
            lock (childGate)
            {
                if (disposed) return;
                ReadTimes();
                registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) =>
                {
                    var observed = Stopwatch.GetTimestamp();
                    lock (childGate) { if (disposed) return; observerQpc = observed; ReadTimes(); }
                }, null, Timeout.Infinite, executeOnlyOnce: true);
            }
        }
        private void ReadTimes()
        {
            var native = handle.SafeWaitHandle.DangerousGetHandle();
            var signaled = WaitForSingleObject(native, 0) == 0;
            if (!GetProcessTimes(native, out var created, out var exit, out var system, out var application))
            { Interlocked.Increment(ref failures); return; }
            creation = created; kernel = system; user = application;
            if (signaled)
            {
                exited = exit;
                if (GetExitCodeProcess(native, out var code)) exitCode = code;
            }
        }
        public object Finish()
        {
            lock (childGate)
            {
                ReadTimes();
                Dispose();
                var result = new
                {
                    pid, state.OperationId, state.CommandClass, state.StartCallQpc, state.StartCallUtc, state.ClockPairAfterQpc,
                    startReturnedQpc = returnedQpc, exitObserverQpc = observerQpc,
                    osCreationFileTimeUtc = creation, osExitFileTimeUtc = exited,
                    osCreationUtc = creation is { } start ? DateTime.FromFileTimeUtc(start) : (DateTime?)null,
                    osExitUtc = exited is { } end ? DateTime.FromFileTimeUtc(end) : (DateTime?)null,
                    osLifetimeMs = creation is { } c && exited is { } e ? (e - c) / 10000.0 : (double?)null,
                    kernelCpuMs = kernel / 10000.0, userCpuMs = user / 10000.0, exitCode,
                    handleSignaledAtReport = exited is not null, duplicateReleased = handle.SafeWaitHandle.IsClosed
                };
                return result;
            }
        }
        public void Dispose()
        {
            lock (childGate)
            { if (disposed) return; disposed = true; registration?.Unregister(null); handle.Dispose(); }
        }
    }
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeProcessHandle sourceHandle, IntPtr targetProcess, out IntPtr duplicate, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr handle, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
internal static class RunScopePatch
{
    public static void Prefix(out long __state) => __state = ProcessLifecycle.PushScope();
    public static void Finalizer(long __state) => ProcessLifecycle.PopScope(__state);
}
internal static class NotifyPatch
{
    public static void Prefix(object entry) => ProcessLifecycle.Notify(entry);
    public static void Finalizer() { }
}
internal static class ProcessStartPatch
{
    public static void Prefix(Process __instance, out ProcessLifecycle.StartState? __state) => __state = ProcessLifecycle.BeforeStart(__instance);
    public static void Finalizer(Process __instance, ProcessLifecycle.StartState? __state, bool __result, Exception? __exception)
        => ProcessLifecycle.AfterStart(__instance, __state, __result, __exception);
}
