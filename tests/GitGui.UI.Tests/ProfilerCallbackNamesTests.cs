using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class ProfilerCallbackNamesTests
{
    [Fact]
    public void Names_preserve_the_original_metadata_format()
    {
        Action closure = () => GC.KeepAlive(this);
        MethodBase[] methods = [closure.Method, typeof(List<int>).GetMethod("Clear")!,
            new DynamicMethod("WithoutDeclaringType", typeof(void), Type.EmptyTypes)];
        foreach (var method in methods)
        {
            var expected = method.DeclaringType?.FullName + "." + method.Name;
            Parallel.For(0, 100, _ => Assert.Equal(expected, CallbackNames.For(method)));
        }
    }

    [Fact]
    public void Repeated_callbacks_reuse_names_without_per_span_string_allocations()
    {
        Action callback = GC.Collect;
        var name = CallbackNames.For(callback.Method);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
            GC.KeepAlive(CallbackNames.For(callback.Method));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4096);
        Assert.Same(name, CallbackNames.For(callback.Method));
    }
}
