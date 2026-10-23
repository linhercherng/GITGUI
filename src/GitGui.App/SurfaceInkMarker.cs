using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;

namespace GitGui.App;

internal sealed class SurfaceInkMarker : IDisposable
{
    private readonly Window window;
    private readonly CompositionContainerVisual root;
    private readonly CompositionContainerVisual container;
    private readonly InkHandler ink = new();
    private readonly CompositionCustomVisual marker;
    private readonly CompositionVisual? previous;
    private bool movedPrevious, attached, disposed;
    public Rect Bounds { get; }
    public string RepaintTargetType { get; }

    public SurfaceInkMarker(Window window)
    {
        this.window = window;
        var scale = window.RenderScaling;
        if (!window.IsVisible || !window.IsEffectivelyVisible || window.WindowState == WindowState.Minimized ||
            window.Opacity != 1 || !double.IsFinite(scale) || scale <= 0 ||
            !double.IsFinite(window.ClientSize.Width) || !double.IsFinite(window.ClientSize.Height) ||
            window.ClientSize.Width * scale < 3 || window.ClientSize.Height * scale < 3)
            throw new InvalidOperationException("The probe root is not a visible, non-minimized opaque rendering target.");
        Bounds = new(Math.Floor(window.ClientSize.Width * scale / 2) / scale,
            Math.Floor(window.ClientSize.Height * scale / 2) / scale, 1 / scale, 1 / scale);
        if (window.Clip is { } clip && new[] { Bounds.TopLeft, Bounds.TopRight, Bounds.BottomLeft, Bounds.BottomRight, Bounds.Center }.Any(point => !clip.FillContains(point)))
            throw new InvalidOperationException("The probe root clips the surface marker.");
        root = ElementComposition.GetElementVisual(window) as CompositionContainerVisual
            ?? throw new InvalidOperationException("The probe root has no composition container.");
        var compositor = root.Compositor;
        previous = ElementComposition.GetElementChildVisual(window);
        container = compositor.CreateContainerVisual(); container.Size = new(window.ClientSize.Width, window.ClientSize.Height);
        marker = compositor.CreateCustomVisual(ink);
        marker.Size = new(Bounds.Width, Bounds.Height); marker.Offset = new(Bounds.X, Bounds.Y, 0);
        RepaintTargetType = marker.GetType().FullName!;
        try
        {
            // Keep an existing custom visual in the same position and z-order. All public
            // composition changes are serialized together after the renderer's scene update.
            if (previous is not null)
            {
                movedPrevious = root.Children.Remove(previous);
                container.Children.Add(previous);
            }
            container.Children.Add(marker);
            ElementComposition.SetElementChildVisual(window, container); attached = true;
        }
        catch { Dispose(); throw; }
    }

    public long RequireRendered(long sceneQpc, long completedQpc)
    {
        var drawn = ink.FirstDrawQpc;
        if (drawn == 0 || drawn < sceneQpc || drawn > completedQpc)
            throw new InvalidOperationException($"The probe root marker was not drawn in its scene batch (scene={sceneQpc}, ink={drawn}, completed={completedQpc}).");
        return drawn;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (previous is not null) container.Children.Remove(previous);
        container.Children.Remove(marker);
        if (attached)
        {
            if (!ReferenceEquals(ElementComposition.GetElementChildVisual(window), container))
                throw new InvalidOperationException("The probe composition marker lost ownership of its slot.");
            ElementComposition.SetElementChildVisual(window, previous);
        }
        else if (movedPrevious && previous is not null) root.Children.InsertAtTop(previous);
    }

    private sealed class InkHandler : CompositionCustomVisualHandler
    {
        private long firstDrawQpc;
        public long FirstDrawQpc => Volatile.Read(ref firstDrawQpc);
        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(Brushes.Magenta, null, new Rect(0, 0, EffectiveSize.X, EffectiveSize.Y));
            Interlocked.CompareExchange(ref firstDrawQpc, Stopwatch.GetTimestamp(), 0);
        }
    }
}
