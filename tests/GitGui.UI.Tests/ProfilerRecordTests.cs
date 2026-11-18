using System.Runtime.CompilerServices;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class ProfilerRecordTests
{
    [Fact]
    public void Hot_buffer_has_no_gc_references_and_projection_is_lossless()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<RawSpanRecord>());
        var strings = new SpanStrings();
        var raw = new RawSpanRecord { Id = 19, Parent = 4, Seam = strings.Intern("WindowImpl.AppWndProc"),
            Callback = strings.Intern("中文.回呼#名稱"), Phase = strings.Intern("status1.wait-publication"),
            ExitPhase = strings.Intern("status1.ready"), ExceptionType = strings.Intern("System.Exception"),
            EnterQpc = 5829598577453, ExitQpc = 5829598811410, NativeTid = 123, Completed = true };
        var record = raw.Project(strings);
        Assert.Equal(19, record.Id); Assert.Equal(4, record.Parent);
        Assert.Equal("WindowImpl.AppWndProc", record.Seam); Assert.Equal("中文.回呼#名稱", record.Callback);
        Assert.Equal("status1.wait-publication", record.Phase); Assert.Equal("status1.ready", record.ExitPhase);
        Assert.Equal("System.Exception", record.ExceptionType);
        Assert.Equal(raw.EnterQpc, record.EnterQpc); Assert.Equal(raw.ExitQpc, record.ExitQpc);
        Assert.Equal(raw.NativeTid, record.NativeTid); Assert.True(record.Completed);
        Assert.Null(record.ProductPhaseAtEnter); Assert.Null(record.ProductPhaseAtExit);
        var empty = default(RawSpanRecord).Project(strings);
        Assert.Null(empty.Callback); Assert.Null(empty.ExceptionType); Assert.False(empty.Completed);
    }

    [Fact]
    public void String_ids_preserve_null_case_unicode_and_concurrent_identity()
    {
        var strings = new SpanStrings();
        Assert.Equal(0, strings.Intern(null)); Assert.Null(strings.Resolve(0));
        var id = strings.Intern("中文#callback");
        Parallel.For(0, 10_000, _ => Assert.Equal(id, strings.Intern("中文#callback")));
        Assert.Equal("中文#callback", strings.Resolve(id));
        Assert.NotEqual(strings.Intern("Callback"), strings.Intern("callback"));
        Assert.NotEqual(0, strings.Intern("")); Assert.Equal("", strings.Resolve(strings.Intern("")));
        for (var i = 0; i < 10_000; i++) strings.Intern("中文#callback");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++) strings.Intern("中文#callback");
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4096);
    }
}
