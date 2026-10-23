using System.Diagnostics;
using System.Diagnostics.Metrics;
using GitGui.App;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class NativePerformanceMetricsTests
{
    [Fact]
    public void Collector_retains_every_histogram_sample_with_phase_clock_and_thread_identity()
    {
        var name = "test.duration." + Guid.NewGuid().ToString("N");
        using var collector = new NativePerformanceMetrics();
        collector.BindUiThread(); collector.SetPhase("diff.read");
        using var meter = new Meter(NativePerformanceMetrics.MeterName);
        var histogram = meter.CreateHistogram<double>(name, "ms");
        var before = Stopwatch.GetTimestamp();
        histogram.Record(.25); histogram.Record(16); histogram.Record(37.51);
        var after = Stopwatch.GetTimestamp();
        var samples = collector.Snapshot().Where(s => s.Instrument == name).ToArray();
        Assert.Equal([.25, 16, 37.51], samples.Select(s => s.Value));
        Assert.All(samples, sample =>
        {
            Assert.Equal("ms", sample.Unit); Assert.Equal("diff.read", sample.Phase);
            Assert.InRange(sample.RecordedQpc, before, after);
            Assert.Equal(Environment.CurrentManagedThreadId, sample.ManagedThreadId);
            Assert.True(sample.IsUiThread);
            if (OperatingSystem.IsWindows()) Assert.True(sample.NativeThreadId > 0);
            else Assert.Null(sample.NativeThreadId);
        });
        Assert.Equal(Stopwatch.Frequency, collector.TimestampFrequency);
    }

    [Fact]
    public async Task Collector_identifies_background_measurements_without_discarding_them()
    {
        var name = "test.background." + Guid.NewGuid().ToString("N");
        using var collector = new NativePerformanceMetrics(); collector.BindUiThread();
        using var meter = new Meter(NativePerformanceMetrics.MeterName);
        var histogram = meter.CreateHistogram<double>(name, "ms");
        await Task.Factory.StartNew(() => histogram.Record(24), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var sample = Assert.Single(collector.Snapshot(), s => s.Instrument == name);
        Assert.False(sample.IsUiThread); Assert.Equal(24, sample.Value);
    }

    [Fact]
    public void Collector_excludes_other_meters_and_retains_observable_counter_values()
    {
        var name = "test.counter." + Guid.NewGuid().ToString("N");
        using var collector = new NativePerformanceMetrics();
        using var ignored = new Meter("Unrelated." + Guid.NewGuid().ToString("N"));
        ignored.CreateHistogram<double>(name).Record(99);
        using var meter = new Meter(NativePerformanceMetrics.MeterName);
        meter.CreateObservableUpDownCounter(name, () => 123, "{visual}");
        collector.RecordObservableInstruments();
        var sample = Assert.Single(collector.Snapshot(), s => s.Instrument == name);
        Assert.Equal(123, sample.Value); Assert.Equal("{visual}", sample.Unit);
    }

    [Fact]
    public void Disposing_listener_stops_collection_and_preserves_existing_samples()
    {
        var name = "test.dispose." + Guid.NewGuid().ToString("N");
        using var meter = new Meter(NativePerformanceMetrics.MeterName);
        var histogram = meter.CreateHistogram<double>(name, "ms");
        var collector = new NativePerformanceMetrics();
        histogram.Record(5); collector.Dispose(); collector.Dispose(); histogram.Record(50);
        Assert.Equal(5, Assert.Single(collector.Snapshot(), s => s.Instrument == name).Value);
    }
}
