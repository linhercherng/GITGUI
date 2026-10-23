using GitGui.Core;
using Xunit;

namespace GitGui.Core.Tests;

public class FallbackTests
{
    [Fact]
    public async Task Unexpected_library_errors_fall_back_to_cli()
    {
        var cli = new FakeCli();
        var reader = new RoutedRepoReader(cli, new BrokenLibrary());
        Assert.Equal("fallback", Assert.Single(await reader.GetLogAsync("repo", 1, default)).Subject);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_by_fallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = new RoutedRepoReader(new FakeCli(), new BrokenLibrary());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.GetLogAsync("repo", 1, cancellation.Token));
    }

    private sealed class BrokenLibrary : IHistoryReader
    {
        public Task<IReadOnlyList<CommitInfo>> GetLogAsync(string repository, int limit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }
    }

    private sealed class FakeCli : IRepoReader
    {
        public Task<RepoFormat> GetFormatAsync(string repository, CancellationToken ct) => Task.FromResult(new RepoFormat("sha1", "files"));
        public Task<IReadOnlyList<CommitInfo>> GetLogAsync(string repository, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CommitInfo>>([new("abc", "fallback", "test", DateTimeOffset.UnixEpoch, [])]);
        public Task<RepoStatus> GetStatusAsync(string repository, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetDiffAsync(string repository, string path, bool staged, CancellationToken ct) => throw new NotSupportedException();
    }
}
