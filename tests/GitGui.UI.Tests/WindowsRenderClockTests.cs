using GitGui.App;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class WindowsRenderClockTests
{
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
