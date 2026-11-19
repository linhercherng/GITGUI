using GitGui.App;
using System.Runtime.InteropServices;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class WindowsRenderClockTests
{
    [Fact]
    public async Task Windows_11_render_worker_keeps_automatic_qos_without_changing_process_policy_or_thread_priority()
    {
        // The QoS query classes require build 22000; older hosts still run the clock lifecycle tests.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var processBefore = ReadProcessPolicy(process.Handle);
        using var clock = new WindowsHighResolutionRenderClock();
        var received = new TaskCompletionSource<(PowerPolicy Policy, ThreadPriority Priority)>(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.Tick += _ =>
        {
            try { received.TrySetResult((ReadPowerPolicy(), Thread.CurrentThread.Priority)); }
            catch (Exception error) { received.TrySetException(error); }
        };
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0u, actual.Policy.ControlMask & 1u);
        Assert.Equal(0u, actual.Policy.StateMask & 1u);
        Assert.Equal(ThreadPriority.Normal, actual.Priority);
        Assert.Equal(processBefore, ReadProcessPolicy(process.Handle));
        clock.Dispose();
        await clock.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerPolicy { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadInformation(nint thread, int kind, ref PowerPolicy state, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(nint process, int kind, ref PowerPolicy state, uint size);
    private static PowerPolicy ReadPowerPolicy()
    {
        var policy = new PowerPolicy { Version = 1 };
        Assert.True(GetThreadInformation(GetCurrentThread(), 3, ref policy, 12), $"Thread QoS query failed: {Marshal.GetLastWin32Error()}");
        return policy;
    }
    private static PowerPolicy ReadProcessPolicy(nint process)
    {
        var policy = new PowerPolicy { Version = 1 };
        Assert.True(GetProcessInformation(process, 4, ref policy, 12), $"Process QoS query failed: {Marshal.GetLastWin32Error()}");
        return policy;
    }

    [Theory]
    [InlineData(10, 10, 19, 20)]
    [InlineData(10, 10, 20, 30)]
    [InlineData(10, 10, 23, 30)]
    [InlineData(10, 10, 49, 50)]
    public void Delayed_ticks_skip_expired_deadlines_without_delaying_a_whole_extra_frame(
        double previous, double interval, long now, double expected)
        => Assert.Equal(expected, WindowsHighResolutionRenderClock.NextDeadline(previous, interval, now));

    [Fact]
    public async Task Unsubscribe_and_resubscribe_keeps_a_live_clock_and_disposal_stops_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var clock = new WindowsHighResolutionRenderClock();
        for (var i = 0; i < 20; i++)
        {
            var received = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Tick(TimeSpan time) => received.TrySetResult(time);
            clock.Tick += Tick;
            Assert.True(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)) > TimeSpan.Zero);
            clock.Tick -= Tick;
        }
        clock.Dispose();
        await clock.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => clock.Tick += _ => { });
        clock.Dispose();
    }

    [Fact]
    public async Task An_unused_clock_disposes_without_starting_a_worker()
    {
        if (!OperatingSystem.IsWindows()) return;
        var clock = new WindowsHighResolutionRenderClock();
        clock.Dispose();
        await clock.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Tick -= _ => { };
        Assert.Throws<ObjectDisposedException>(() => clock.Tick += _ => { });
    }

    [Fact]
    public async Task Concurrent_subscriptions_and_shutdown_do_not_lose_the_stop_signal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var clock = new WindowsHighResolutionRenderClock();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Tick(TimeSpan _) => received.TrySetResult();
        clock.Tick += Tick;
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) { clock.Tick += Tick; clock.Tick -= Tick; }
        })));
        clock.Tick -= Tick;
        clock.Dispose();
        await clock.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
