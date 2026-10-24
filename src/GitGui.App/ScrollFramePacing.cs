namespace GitGui.App;

internal static class ScrollFramePacing
{
    // RAF can run before an earlier composition batch has reached the surface.
    // Retain both signals: neither an empty RAF loop nor a batch alone is a frame.
    public static Task WaitAsync(Task animationCallback, Task renderedBatch, CancellationToken cancellationToken)
        => Task.WhenAll(animationCallback, renderedBatch).WaitAsync(cancellationToken);
}
