using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.Rendering;

namespace GitGui.UI;

/// <summary>Attach visible line controls in bounded batches, retaining the real document.</summary>
internal sealed class IncrementalTextView(Action<Action>? scheduleFrame = null) : TextView
{
    protected override Type StyleKeyOverride => typeof(TextView);
    private const int RowsPerFrame = 8;
    private TaskCompletionSource? ready;
    private int rows, version;
    private bool pending, scheduled, resumeOnAttach;
    internal Task DisplayReady => ready?.Task ?? Task.CompletedTask;

    internal void BeginUpdate()
    {
        ready?.TrySetCanceled();
        ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rows = RowsPerFrame; pending = true; scheduled = false; version++;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!pending || availableSize.Height <= 0)
            return base.MeasureOverride(availableSize);
        if (!double.IsFinite(availableSize.Height))
        {
            var complete = base.MeasureOverride(availableSize);
            pending = false; ready!.TrySetResult(); return complete;
        }
        var height = Math.Min(availableSize.Height, Math.Max(1, DefaultLineHeight) * (rows - 0.001));
        var desired = base.MeasureOverride(availableSize.WithHeight(height));
        if (height >= availableSize.Height || DocumentHeight <= height)
        {
            pending = false; ready!.TrySetResult();
        }
        else if (!scheduled)
        {
            var current = version;
            void Continue()
            {
                if (current != version || !pending) return;
                scheduled = false; rows += RowsPerFrame; InvalidateMeasure();
            }
            if (scheduleFrame is not null) { scheduled = true; scheduleFrame(Continue); }
            else if (TopLevel.GetTopLevel(this) is { } top)
            { scheduled = true; top.RequestAnimationFrame(_ => Continue()); }
        }
        // Reserve the complete viewport while its line controls are attached;
        // only readiness, never the document or final viewport size, is sliced.
        return desired.WithHeight(Math.Min(availableSize.Height, DocumentHeight));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (pending)
        {
            ready?.TrySetCanceled(); version++; pending = scheduled = false; resumeOnAttach = true;
        }
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (resumeOnAttach) { resumeOnAttach = false; BeginUpdate(); }
    }
}
