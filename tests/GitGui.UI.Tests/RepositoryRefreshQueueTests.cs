using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class RepositoryRefreshQueueTests
{
    [Theory]
    [InlineData("fsmonitor--daemon")]
    [InlineData("fsmonitor--daemon/cookies/123-4")]
    [InlineData("fsmonitor--daemon/cookies/temporary-cookie")]
    [InlineData("fsmonitor--daemon.log")]
    [InlineData("fsmonitor--daemon.log.1")]
    [InlineData("fsmonitor--daemon.ipc")]
    [InlineData("fsmonitor--daemon.ipc.123.tmp")]
    public void Fsmonitor_communication_metadata_is_ignored_in_git_and_shared_common_directories(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "watcher-linked-worktree");
        var admin = Path.Combine(Path.GetTempPath(), "watcher-common", "worktrees", "linked"); var common = Path.Combine(Path.GetTempPath(), "watcher-common");
        Assert.Equal(RepositoryRefreshKind.None, RepositoryRefreshQueue.Classify(root, admin, common, Path.Combine(admin, relative)));
        Assert.Equal(RepositoryRefreshKind.None, RepositoryRefreshQueue.Classify(root, admin, common, Path.Combine(common, relative)));
        Assert.Equal(RepositoryRefreshKind.Status, RepositoryRefreshQueue.Classify(root, admin, common, Path.Combine(root, relative)));
    }

    [Fact]
    public void Daemon_cookie_feedback_does_not_schedule_or_postpone_refresh_and_real_changes_still_do()
    {
        var root = Path.Combine(Path.GetTempPath(), "watcher-repo"); var admin = Path.Combine(root, ".git"); var queue = new RepositoryRefreshQueue();
        RepositoryRefreshKind Kind(string relative) => RepositoryRefreshQueue.Classify(root, admin, admin, Path.Combine(root, relative));
        for (var i = 0; i < 100; i++) Assert.False(queue.Add(Kind(".git/fsmonitor--daemon/cookies/cookie-" + i), i));
        Assert.False(queue.Take(1000, false, out _, out _));
        Assert.True(queue.Add(Kind("tracked.txt"), 1000));
        Assert.False(queue.Add(Kind(".git/fsmonitor--daemon.log"), 1299));
        Assert.True(queue.Take(1300, false, out var kind, out _)); Assert.Equal(RepositoryRefreshKind.Status, kind);
        Assert.False(queue.Add(Kind(".git/fsmonitor--daemon.ipc"), 1500)); Assert.False(queue.Take(2000, false, out _, out _));
        Assert.Equal(RepositoryRefreshKind.Status, Kind("fsmonitor--daemon.log"));
        Assert.Equal(RepositoryRefreshKind.Status | RepositoryRefreshKind.Operation, Kind(".git/fsmonitor--daemon-notes"));
        Assert.Equal(RepositoryRefreshKind.Status | RepositoryRefreshKind.Operation, Kind(".git/info/fsmonitor--daemon.log"));
        Assert.Equal(RepositoryRefreshKind.Status, Kind(".git/index"));
    }
    [Fact]
    public void Fifty_thousand_events_post_once_and_busy_operations_keep_one_pending_refresh()
    {
        var queue = new RepositoryRefreshQueue(); var posts = 0;
        for (var i = 0; i < 50_000; i++) if (queue.Add(RepositoryRefreshKind.Status, 0)) posts++;
        Assert.Equal(1, posts);
        Assert.False(queue.Take(300, true, out _, out _));
        Assert.False(queue.Add(RepositoryRefreshKind.History, 350));
        Assert.False(queue.Take(500, false, out _, out var delay)); Assert.Equal(150, delay);
        Assert.True(queue.Take(650, false, out var kinds, out _));
        Assert.Equal(RepositoryRefreshKind.Status | RepositoryRefreshKind.History, kinds);
        Assert.False(queue.Take(1000, false, out _, out _));
        Assert.True(queue.Add(RepositoryRefreshKind.Status, 1000));
    }
    [Fact]
    public void Worktree_changes_do_not_reload_history_and_object_writes_do_not_refresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "watcher-repo"); var admin = Path.Combine(root, ".git");
        RepositoryRefreshKind Kind(string relative) => RepositoryRefreshQueue.Classify(root, admin, admin, Path.Combine(root, relative));
        Assert.Equal(RepositoryRefreshKind.Status, Kind("tracked.txt"));
        Assert.Equal(RepositoryRefreshKind.Status, Kind(".git/index"));
        Assert.Equal(RepositoryRefreshKind.None, Kind(".git/objects/ab/1234"));
        Assert.Equal(RepositoryRefreshKind.None, Kind(".git/refs/gitgui/snapshots/1"));
        Assert.True(Kind(".git/refs/heads/main").HasFlag(RepositoryRefreshKind.History));
        Assert.True(Kind(".git/shallow").HasFlag(RepositoryRefreshKind.History));
        Assert.True(Kind(".git/info/grafts").HasFlag(RepositoryRefreshKind.History));
        Assert.True(Kind(".git/refs/replace/1234").HasFlag(RepositoryRefreshKind.History));
        Assert.Equal(RepositoryRefreshKind.All, Kind(".git/config"));
    }
}
