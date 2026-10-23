namespace GitGui.App;

internal sealed record RenderBatchCompletion(long BatchId, long RequestedQpc, long CompletedQpc,
    bool CompletedSuccessfully, string Phase, string? ActiveGpuContextType, string? InspectionError);
internal sealed record SubmittedRenderFrame(long RenderPassQpc, long CompletedQpc, long BatchId, double RenderDurationMs);
internal sealed record ScrollCadenceGate(bool Pass, int ExpectedMinimumFrames, int ObservedFrames,
    int MissedInternalOpportunities, double NominalFps, double FractionalRefreshHz, double GapLimitMs,
    string[] Errors);
internal sealed record ScrollRenderReport(long StartQpc, long EndQpc, long TimestampFrequency, double ElapsedMs,
    int RenderPassCount, int CompletedBatchCount, SubmittedRenderFrame[] FramesInOrder,
    double[] CompletionIntervalsMs, double[] RenderPassIntervalsMs, double SubmittedFramesPerSecond,
    ScrollCadenceGate Gate, double? StartToFirstCompletionMs, double? LastCompletionToEndMs);

internal static class ScrollRenderEvidence
{
    public const string RenderInstrument = "avalonia.comp.render.time";
    public static ScrollRenderReport Evaluate(long start, long end, long frequency,
        IEnumerable<NativeMetricSample> metrics, IEnumerable<RenderBatchCompletion> batches,
        IEnumerable<string> rendererErrors)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        if (end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        var errors = rendererErrors.ToList();
        var passes = metrics.Where(s => s.Instrument == RenderInstrument && s.RecordedQpc >= start && s.RecordedQpc < end)
            .OrderBy(s => s.RecordedQpc).ToArray();
        if (passes.Any(p => p.Unit != "ms" || !double.IsFinite(p.Value) || p.Value < 0)) errors.Add("Invalid render histogram measurement.");
        var completed = batches.GroupBy(b => b.BatchId).Select(g => g.OrderBy(b => b.CompletedQpc).First())
            .OrderBy(b => b.CompletedQpc).ToArray();
        if (completed.Any(b => !b.CompletedSuccessfully)) errors.Add("A composition batch did not complete successfully.");
        var used = new HashSet<long>();
        var frames = new List<SubmittedRenderFrame>();
        foreach (var batch in completed)
        {
            if (!batch.CompletedSuccessfully || batch.CompletedQpc < start || batch.CompletedQpc >= end) continue;
            // Multiple UI requests may share one batch; one pass can only establish one frame.
            var pass = passes.LastOrDefault(p => p.RecordedQpc >= batch.RequestedQpc && p.RecordedQpc <= batch.CompletedQpc && !used.Contains(p.RecordedQpc));
            if (pass is null) continue;
            used.Add(pass.RecordedQpc);
            frames.Add(new(pass.RecordedQpc, batch.CompletedQpc, batch.BatchId, pass.Value));
        }
        var frameArray = frames.ToArray();
        double[] Intervals(IEnumerable<long> timestamps)
        {
            var values = timestamps.ToArray();
            return values.Zip(values.Skip(1), (before, after) => (after - before) * 1000d / frequency).ToArray();
        }
        var completionIntervals = Intervals(frameArray.Select(f => f.CompletedQpc));
        var renderIntervals = Intervals(passes.Select(p => p.RecordedQpc));
        var durationMs = (end - start) * 1000d / frequency;
        const double fractionalRefresh = 60000d / 1001;
        var frameMs = 1000 / fractionalRefresh;
        double? startGap = frameArray.Length == 0 ? null : (frameArray[0].CompletedQpc - start) * 1000d / frequency;
        double? endGap = frameArray.Length == 0 ? null : (end - frameArray[^1].CompletedQpc) * 1000d / frequency;
        // Counts and internal intervals alone can hide a burst and silent head/tail.
        if (startGap >= 1.5 * frameMs) errors.Add("Silent scroll start endpoint exceeds the frame-gap limit.");
        if (endGap >= 1.5 * frameMs) errors.Add("Silent scroll end endpoint exceeds the frame-gap limit.");
        // At most two partially observed endpoint frames; never discount an internal gap.
        var expected = Math.Max(0, (int)Math.Floor(durationMs / frameMs) - 1);
        var missed = completionIntervals.Sum(gap => gap >= 1.5 * frameMs ? Math.Max(1, (int)Math.Floor(gap / frameMs + .5) - 1) : 0);
        if (durationMs < 5000) errors.Add("Scroll interval is shorter than five seconds.");
        var passGate = errors.Count == 0 && frames.Count >= expected && missed == 0 && frames.Count > 1;
        return new(start, end, frequency, durationMs, passes.Length, completed.Length, frameArray,
            completionIntervals, renderIntervals, frames.Count / (durationMs / 1000),
            new(passGate, expected, frames.Count, missed, 60, fractionalRefresh, 1.5 * frameMs, errors.ToArray()), startGap, endGap);
    }
}
