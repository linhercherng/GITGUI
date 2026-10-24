using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class SnapshotReadSchedulingTests
{
    [Fact]
    public async Task Snapshot_catalog_read_runs_outside_the_callers_synchronization_context()
    {
        var runner = new GitProcessRunner();
        var catalog = new ContextCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var previous = SynchronizationContext.Current;
        Task<IReadOnlyList<SnapshotInfo>> reading;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            reading = service.GetSnapshotsAsync("test-root", default);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.Empty(await reading);
        Assert.Equal("test-root", catalog.Root);
        Assert.Null(catalog.Context);
    }

    private sealed class ContextCatalog : ISnapshotCatalog
    {
        public SynchronizationContext? Context { get; private set; }
        public string? Root { get; private set; }
        public Task<IReadOnlyList<SnapshotInfo>> ListAsync(string root, CancellationToken ct)
        {
            Context = SynchronizationContext.Current; Root = root;
            return Task.FromResult<IReadOnlyList<SnapshotInfo>>([]);
        }
        public Task SaveAsync(SnapshotInfo snapshot, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
}
