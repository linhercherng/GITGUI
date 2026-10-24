using GitGui.App;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class ScrollFramePacingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Neither_callback_nor_render_completion_alone_advances_scrolling(bool callbackFirst)
    {
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var step = ScrollFramePacing.WaitAsync(callback.Task, rendered.Task, default);
        (callbackFirst ? callback : rendered).SetResult();
        Assert.False(step.IsCompleted);
        (callbackFirst ? rendered : callback).SetResult();
        await step.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_frozen_renderer_can_be_cancelled_without_accepting_an_animation_callback()
    {
        using var cancellation = new CancellationTokenSource();
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var step = ScrollFramePacing.WaitAsync(Task.CompletedTask, rendered.Task, cancellation.Token);
        Assert.False(step.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step);
    }

    [Fact]
    public async Task A_failed_render_cannot_be_reported_as_a_completed_frame()
    {
        var failure = new InvalidOperationException("surface failure");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScrollFramePacing.WaitAsync(Task.CompletedTask, Task.FromException(failure), default));
        Assert.Same(failure, actual);
    }
}
