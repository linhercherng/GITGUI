using GitGui.App;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class ScrollRenderEvidenceTests
{
    private const long Frequency = 1_000_000;
    private static NativeMetricSample Sample(long qpc) => new(ScrollRenderEvidence.RenderInstrument, "ms", .5, qpc, 2, 2, false, "graph-scroll");
    private static RenderBatchCompletion Batch(long id, long qpc) => new(id, qpc - 1000, qpc + 500, true, "graph-scroll", "WinUiContext", null);
    private static ScrollRenderReport Evaluate(long[] times, string[]? errors = null) => ScrollRenderEvidence.Evaluate(0, 5 * Frequency, Frequency,
        times.Select(Sample), times.Select((time, i) => Batch(i + 1, time)), errors ?? []);
    private static long[] FractionalCadence() => Enumerable.Range(1, 299).Select(i => (long)(i * Frequency * 1001d / 60000)).ToArray();

    [Fact]
    public void Nominal_sixty_accepts_fractional_refresh_and_partial_endpoint_frames()
    {
        var report = Evaluate(FractionalCadence()[..^1].Select(time => time + 7000).ToArray());
        Assert.True(report.Gate.Pass); Assert.Equal(298, report.Gate.ExpectedMinimumFrames);
        Assert.Equal(298, report.Gate.ObservedFrames); Assert.Equal(0, report.Gate.MissedInternalOpportunities);
        Assert.InRange(report.StartToFirstCompletionMs!.Value, 0, report.Gate.GapLimitMs);
        Assert.InRange(report.LastCompletionToEndMs!.Value, 0, report.Gate.GapLimitMs);
    }

    [Fact]
    public void A_compensated_dropped_frame_fails_even_when_average_count_recovers()
    {
        var times = FractionalCadence().ToList();
        times.RemoveAt(100); times.Insert(150, times[149] + 1000);
        var report = Evaluate(times.ToArray());
        Assert.True(report.Gate.ObservedFrames >= report.Gate.ExpectedMinimumFrames);
        Assert.False(report.Gate.Pass); Assert.True(report.Gate.MissedInternalOpportunities > 0);
        Assert.Equal(times.Count - 1, report.CompletionIntervalsMs.Length);
    }

    [Theory]
    [InlineData(20_000)]
    [InlineData(4_680_000)]
    public void An_early_or_late_burst_cannot_hide_a_silent_endpoint(long offset)
    {
        var times = Enumerable.Range(0, 299).Select(i => offset + i * 1000L).ToArray();
        var report = Evaluate(times);
        Assert.True(report.Gate.ObservedFrames >= report.Gate.ExpectedMinimumFrames);
        Assert.Equal(0, report.Gate.MissedInternalOpportunities);
        Assert.False(report.Gate.Pass);
        Assert.Contains(report.Gate.Errors, error => error.Contains("endpoint", StringComparison.Ordinal));
        Assert.True(report.StartToFirstCompletionMs >= report.Gate.GapLimitMs || report.LastCompletionToEndMs >= report.Gate.GapLimitMs);
    }

    [Fact]
    public void Exact_qpc_interval_excludes_phase_label_samples_outside_the_window()
    {
        var inside = FractionalCadence();
        var samples = inside.Select(Sample).Append(Sample(-1)).Append(Sample(5 * Frequency));
        var batches = inside.Select((t, i) => Batch(i + 1, t));
        var report = ScrollRenderEvidence.Evaluate(0, 5 * Frequency, Frequency, samples, batches, []);
        Assert.Equal(inside.Length, report.RenderPassCount);
    }

    [Fact]
    public void Duplicate_batch_callbacks_and_repeated_pass_observation_do_not_inflate_frames()
    {
        var report = ScrollRenderEvidence.Evaluate(0, 5 * Frequency, Frequency, [Sample(10_000)],
            [Batch(1, 10_000), Batch(1, 10_000), Batch(2, 10_000)], []);
        Assert.Single(report.FramesInOrder); Assert.Equal(2, report.CompletedBatchCount);
    }

    [Fact]
    public void Final_drain_completion_outside_the_interval_is_retained_but_not_counted()
    {
        var report = ScrollRenderEvidence.Evaluate(0, 5 * Frequency, Frequency, [Sample(4_999_800)],
            [new(1, 4_999_000, 5_000_300, true, "graph-scroll.draining", null, null)], []);
        Assert.Equal(1, report.RenderPassCount); Assert.Equal(1, report.CompletedBatchCount);
        Assert.Empty(report.FramesInOrder); Assert.False(report.Gate.Pass);
    }

    [Fact]
    public void Renderer_errors_cannot_be_hidden_by_successful_finally_batch_notifications()
    {
        var report = Evaluate(FractionalCadence(), ["Exception when rendering"]);
        Assert.False(report.Gate.Pass); Assert.Contains("Exception when rendering", report.Gate.Errors);
    }

    [Fact]
    public void Render_histograms_without_completed_batches_are_not_submitted_frames()
    {
        var report = ScrollRenderEvidence.Evaluate(0, 5 * Frequency, Frequency, FractionalCadence().Select(Sample), [], []);
        Assert.Empty(report.FramesInOrder); Assert.False(report.Gate.Pass);
    }

    [Fact]
    public void Invalid_timebase_fails_closed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrollRenderEvidence.Evaluate(0, Frequency, 0, [], [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrollRenderEvidence.Evaluate(Frequency, Frequency, Frequency, [], [], []));
    }
}
