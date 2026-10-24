using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GitGui.App;

/// <summary>A process-owned render clock. Never changes global timer resolution or thread priority.</summary>
internal sealed class WindowsHighResolutionRenderClock : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent changed = new(false);
    private readonly TimerWait timer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<TimeSpan>? handlers;
    private Thread? worker;
    private bool disposed;
    internal Task Stopped => stopped.Task;

    public WindowsHighResolutionRenderClock()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var handle = CreateWaitableTimerExW(0, 0, 2, 0x1f0003);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            // Older Windows versions do not support CREATE_WAITABLE_TIMER_HIGH_RESOLUTION.
            handle = CreateWaitableTimerExW(0, 0, 0, 0x1f0003);
        }
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        timer = new(handle);
    }

    public event Action<TimeSpan> Tick
    {
        add
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                handlers += value;
                if (worker is null)
                {
                    worker = new Thread(Loop) { IsBackground = true, Name = "GITGUI Render" };
                    if (OperatingSystem.IsWindows()) worker.SetApartmentState(ApartmentState.STA);
                    worker.Start();
                }
                changed.Set();
            }
        }
        remove { lock (gate) { handlers -= value; if (!disposed) changed.Set(); } }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            handlers = null;
            if (worker is null) Release();
            else changed.Set();
        }
    }

    private void Loop()
    {
        try
        {
            WaitHandle[] waits = [changed, timer];
            var interval = Stopwatch.Frequency / 120d;
            var next = Stopwatch.GetTimestamp() + interval;
            while (true)
            {
                Action<TimeSpan>? action;
                lock (gate) { if (disposed) return; action = handlers; }
                if (action is null)
                {
                    changed.WaitOne();
                    next = Stopwatch.GetTimestamp() + interval;
                    continue;
                }
                var remaining = Math.Max(1, (next - Stopwatch.GetTimestamp()) * 10000000d / Stopwatch.Frequency);
                var due = -(long)Math.Ceiling(remaining);
                if (!SetWaitableTimerEx(timer.SafeWaitHandle, ref due, 0, 0, 0, 0, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (WaitHandle.WaitAny(waits) == 0) continue;
                action(clock.Elapsed);
                next = NextDeadline(next, interval, Stopwatch.GetTimestamp());
            }
        }
        finally { lock (gate) Release(); }
    }

    internal static double NextDeadline(double previous, double interval, long now)
    {
        var next = previous + interval;
        // Skip missed deadlines on the existing cadence. Sleeping a whole interval
        // after a delayed render adds another frame of latency to queued UI work.
        return next <= now ? next + (Math.Floor((now - next) / interval) + 1) * interval : next;
    }

    private void Release()
    {
        timer.Dispose(); changed.Dispose(); stopped.TrySetResult();
    }
    private sealed class TimerWait : WaitHandle
    {
        public TimerWait(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long due, int period, nint callback, nint argument, nint context, uint tolerance);
}

// Avalonia's runtime IRenderTimer is public but intentionally omitted from reference assemblies.
// The official RenderLoop remains responsible for serialization and rendering exception handling.
public class WindowsRenderTimerProxy : DispatchProxy, IDisposable
{
    internal WindowsHighResolutionRenderClock Clock { get; } = new();
    public void Dispose() => Clock.Dispose();
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        switch (method?.Name)
        {
            case "get_RunsInBackground": return true;
            case "add_Tick": Clock.Tick += (Action<TimeSpan>)arguments![0]!; return null;
            case "remove_Tick": Clock.Tick -= (Action<TimeSpan>)arguments![0]!; return null;
            default: throw new NotSupportedException(method?.Name);
        }
    }
}
