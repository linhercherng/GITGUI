using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using GitGui.App;
using SkiaSharp;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class SurfaceInkMarkerTests
{
    [AvaloniaFact]
    public void Empty_text_invalidation_cannot_complete_the_barrier_without_an_actual_render()
    {
        var window = new Window { Content = new TextBlock { Text = "No ink", Foreground = null }, Width = 160, Height = 100 };
        window.Show();
        try
        {
            var compositor = ElementComposition.GetElementVisual(window)!.Compositor;
            var observer = new RenderObserver(); var previous = compositor.CreateCustomVisual(observer); previous.Size = new(160, 100);
            ElementComposition.SetElementChildVisual(window, previous);
            using (window.CaptureRenderedFrame()) { }
            var before = observer.Count;
            using var marker = new SurfaceInkMarker(window);
            Task? rendered = null;
            compositor.RequestCompositionUpdate(() => rendered = compositor.RequestCompositionBatchCommitAsync().Rendered);
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
            Assert.NotNull(rendered); Assert.True(rendered.IsCompletedSuccessfully);
            Assert.True(observer.Count > before, "A successful empty batch must not stand in for actual drawing.");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_completed_batch_contains_new_opaque_ink_even_when_the_text_repaint_has_no_ink(bool existingChild)
    {
        var content = new Border { Background = Brushes.White, Child = new TextBlock { Text = "No text ink", Foreground = null } };
        var window = new Window { Content = content, Width = 160, Height = 100, Background = Brushes.White };
        window.Show();
        try
        {
            var compositor = ElementComposition.GetElementVisual(window)!.Compositor;
            CompositionVisual? previous = null;
            if (existingChild)
            {
                var original = compositor.CreateSolidColorVisual(); original.Color = Colors.Blue;
                original.Size = new(3, 3); original.Offset = new(5, 5, 0);
                previous = original; ElementComposition.SetElementChildVisual(window, previous);
            }
            using (window.CaptureRenderedFrame()) { }
            using (var marker = new SurfaceInkMarker(window))
            {
                // A product scene change and the marker must reach the same committed batch.
                content.Background = Brushes.Lime;
                Task? rendered = null; long sceneQpc = 0;
                compositor.RequestCompositionUpdate(() =>
                {
                    sceneQpc = Stopwatch.GetTimestamp(); rendered = compositor.RequestCompositionBatchCommitAsync().Rendered;
                });
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                Assert.NotNull(rendered); Assert.True(rendered.IsCompletedSuccessfully);
                var completedQpc = Stopwatch.GetTimestamp();
                Assert.InRange(marker.RequireRendered(sceneQpc, completedQpc), sceneQpc, completedQpc);
                using var frame = window.GetLastRenderedFrame(); Assert.NotNull(frame);
                var x = (int)Math.Round(marker.Bounds.X * window.RenderScaling);
                var y = (int)Math.Round(marker.Bounds.Y * window.RenderScaling);
                Assert.Equal(Colors.Magenta, Pixel(frame, x, y));
                Assert.Equal(Colors.Lime, Pixel(frame, x + 2, y));
                if (existingChild) Assert.Equal(Colors.Blue, Pixel(frame, 6, 6));
            }
            Assert.Same(previous, ElementComposition.GetElementChildVisual(window));
            using var restored = window.CaptureRenderedFrame(); Assert.NotNull(restored);
            Assert.Equal(Colors.Lime, Pixel(restored, 80, 50));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void A_disabled_target_cannot_use_a_completed_empty_batch_as_render_evidence()
    {
        var window = new Window { Content = new TextBlock { Text = "Ink" }, Width = 160, Height = 100 };
        window.Show();
        try
        {
            using (window.CaptureRenderedFrame()) { }
            using var marker = new SurfaceInkMarker(window);
            window.Hide();
            Task? rendered = null; long sceneQpc = 0;
            var compositor = ElementComposition.GetElementVisual(window)!.Compositor;
            compositor.RequestCompositionUpdate(() =>
            {
                sceneQpc = Stopwatch.GetTimestamp(); rendered = compositor.RequestCompositionBatchCommitAsync().Rendered;
            });
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
            Assert.NotNull(rendered); Assert.True(rendered.IsCompletedSuccessfully);
            Assert.Throws<InvalidOperationException>(() => marker.RequireRendered(sceneQpc, Stopwatch.GetTimestamp()));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void A_failed_scope_restores_the_previous_child_and_detaches_all_probe_content()
    {
        var window = new Window { Width = 160, Height = 100, Background = Brushes.White };
        window.Show();
        try
        {
            var previous = ElementComposition.GetElementVisual(window)!.Compositor.CreateSolidColorVisual();
            previous.Color = Colors.Blue; previous.Size = new(160, 100);
            ElementComposition.SetElementChildVisual(window, previous);
            using (window.CaptureRenderedFrame()) { }
            CompositionContainerVisual? temporary = null;
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var marker = new SurfaceInkMarker(window);
                temporary = Assert.IsAssignableFrom<CompositionContainerVisual>(ElementComposition.GetElementChildVisual(window));
                throw new InvalidOperationException("Controlled failed measurement.");
            }));
            Assert.Same(previous, ElementComposition.GetElementChildVisual(window));
            Assert.NotNull(temporary); Assert.Empty(temporary.Children);
            using var restored = window.CaptureRenderedFrame(); Assert.NotNull(restored);
            Assert.Equal(Colors.Blue, Pixel(restored, 80, 50));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Disabled_target_is_rejected_and_its_existing_child_is_preserved()
    {
        var window = new Window { Content = new TextBlock { Text = "Hidden ink" }, Width = 160, Height = 100 };
        window.Show();
        try
        {
            var previous = ElementComposition.GetElementVisual(window)!.Compositor.CreateSolidColorVisual();
            ElementComposition.SetElementChildVisual(window, previous); window.Hide();
            Assert.Throws<InvalidOperationException>(() => new SurfaceInkMarker(window));
            Assert.Same(previous, ElementComposition.GetElementChildVisual(window));
        }
        finally { window.Close(); }
    }

    private static Color Pixel(Bitmap bitmap, int x, int y)
    {
        using var stream = new MemoryStream(); bitmap.Save(stream); stream.Position = 0;
        using var image = SKBitmap.Decode(stream); var pixel = image.GetPixel(x, y);
        return Color.FromArgb(pixel.Alpha, pixel.Red, pixel.Green, pixel.Blue);
    }

    private sealed class RenderObserver : CompositionCustomVisualHandler
    {
        public int Count { get; private set; }
        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            Count++; drawingContext.DrawRectangle(Brushes.White, null, new Rect(0, 0, 160, 100));
        }
    }
}
