using GitGui.App;
using Xunit;

namespace GitGui.UI.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void Second_instance_is_rejected_until_the_first_exits()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        using (var first = SingleInstance.TryAcquire(path))
        {
            Assert.NotNull(first);
            SingleInstance? second = null;
            var thread = new Thread(() => { second = SingleInstance.TryAcquire(path); second?.Dispose(); });
            thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(second);
            using var isolated = SingleInstance.TryAcquire(path + "-probe");
            Assert.NotNull(isolated);
        }
        using var reopened = SingleInstance.TryAcquire(path);
        Assert.NotNull(reopened);
    }

    [Fact]
    public void Abandoned_owner_can_be_recovered()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Mutex? abandoned = null;
        var thread = new Thread(() =>
        {
            abandoned = new Mutex(false, SingleInstance.NameFor(path), new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
            abandoned.WaitOne();
        });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        try { using var recovered = SingleInstance.TryAcquire(path); Assert.NotNull(recovered); }
        finally { abandoned?.Dispose(); }
    }
}
