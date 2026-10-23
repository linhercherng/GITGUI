using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using Xunit;

namespace GitGui.Engine.Tests;

public class AdvancedWorkspaceTests
{
    private static WorkspaceService Service(TestInteraction? interaction = null)
    {
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        return new(runner, new TestSettings(), catalog, new SnapshotManager(runner, catalog), interaction ?? new(), fileHistory: new LibHistoryReader());
    }
    private static async Task<OperationOutcome> Do(WorkspaceService service, TemporaryRepository repo, GitOperation op)
    {
        var result = await service.ExecuteAsync(repo.Root, op, null, default);
        Assert.True(result.Success, result.Message); return result;
    }
    private static async Task Commit(TemporaryRepository repo, string file, string text, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo.Root, file), text);
        await repo.Git("add", "--", file); await repo.Git("commit", "-m", message);
    }

    [Fact]
    public async Task Stash_identity_tags_remotes_and_reflog_work_and_validate_before_writing()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var service = Service();
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "work\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "新增.txt"), "new");
        await Do(service, repo, new(GitAction.StashPush) { Message = "保存中文", IncludeUntracked = true });
        var stash = Assert.Single((await service.GetAdvancedStateAsync(repo.Root, default)).Stashes);
        Assert.False(File.Exists(Path.Combine(repo.Root, "新增.txt")));
        var wrong = await service.ExecuteAsync(repo.Root, new(GitAction.StashDrop) { StashOid = new string('0',40) }, null, default);
        Assert.False(wrong.Success); Assert.Null(wrong.Snapshot);
        await Do(service, repo, new(GitAction.StashPop) { StashOid = stash.Oid, Name = stash.Selector });
        Assert.True(File.Exists(Path.Combine(repo.Root, "新增.txt")));
        await Do(service, repo, new(GitAction.CreateTag) { Name = "v中文", Annotated = true, Message = "標籤" });
        await Do(service, repo, new(GitAction.AddRemote) { Name = "test", Url = "../remote.git" });
        await Do(service, repo, new(GitAction.RenameRemote) { Name = "test", NewName = "backup" });
        await Do(service, repo, new(GitAction.SetRemoteUrl) { Name = "backup", Url = "../push.git", PushUrl = true });
        var state = await service.GetAdvancedStateAsync(repo.Root, default);
        Assert.True(Assert.Single(state.Tags).Annotated); Assert.NotEmpty(state.Reflog);
        Assert.Equal("../push.git", Assert.Single(Assert.Single(state.Remotes).PushUrls));
        await Do(service, repo, new(GitAction.RemoveRemote) { Name = "backup" });
        await Do(service, repo, new(GitAction.DeleteTag) { Name = "v中文" });
    }

    [Theory]
    [InlineData(ResetMode.Soft)] [InlineData(ResetMode.Mixed)] [InlineData(ResetMode.Hard)]
    public async Task Reset_modes_preserve_expected_index_and_require_hard_confirmation(ResetMode mode)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await Commit(repo, "tracked.txt", "second\n", "second");
        var interaction = new TestInteraction(); var service = Service(interaction);
        var outcome = await Do(service, repo, new(GitAction.Reset) { Revision = "HEAD~1", ResetMode = mode });
        var status = await repo.Git("status", "--porcelain");
        Assert.Equal(mode == ResetMode.Hard, status.Length == 0);
        if (mode == ResetMode.Soft) Assert.StartsWith("M ", status);
        if (mode == ResetMode.Mixed) Assert.StartsWith(" M", status);
        Assert.Equal(mode == ResetMode.Hard, interaction.Confirmations.Any(c => c.Risk == RiskLevel.High));
        await Do(service, repo, new(GitAction.RestoreSnapshot) { SnapshotId = outcome.Snapshot!.Id });
        Assert.Equal("second", (await repo.Git("log", "-1", "--format=%s")).Trim());
    }

    [Fact]
    public async Task Rebase_reorder_reword_squash_fixup_drop_and_invalid_plan()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var service = Service();
        var upstream = (await repo.Git("rev-parse", "HEAD")).Trim();
        for (var n = 0; n < 6; n++) await Commit(repo, "file" + n, "text" + n, "commit" + n);
        var plan = (await service.GetRebasePlanAsync(repo.Root, upstream, default)).ToArray();
        var stale = await service.ExecuteAsync(repo.Root, new(GitAction.RebaseInteractive) { Revision = upstream, RebasePlan = plan[..^1] }, null, default);
        Assert.False(stale.Success); Assert.Null(stale.Snapshot);
        var ordered = new[] { plan[1], plan[0] with { Action = RebaseStepKind.Reword, Message = "重新命名 中文" },
            plan[2] with { Action = RebaseStepKind.Squash, Message = "合併 中文" }, plan[3] with { Action = RebaseStepKind.Fixup },
            plan[4] with { Action = RebaseStepKind.Drop }, plan[5] };
        await Do(service, repo, new(GitAction.RebaseInteractive) { Revision = upstream, RebasePlan = ordered });
        Assert.Equal("commit1\n合併 中文\ncommit5", (await repo.Git("log", "--reverse", "--format=%s", upstream + "..HEAD")).Trim());
        Assert.False(File.Exists(Path.Combine(repo.Root, "file4")));
    }

    [Fact]
    public async Task Rebase_edit_pause_survives_new_service_and_continue()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var service = Service();
        await Commit(repo, "added", "one", "second");
        var plan = await service.GetRebasePlanAsync(repo.Root, "HEAD~1", default);
        await Do(service, repo, new(GitAction.RebaseInteractive) { Revision = "HEAD~1", RebasePlan = [plan[0] with { Action = RebaseStepKind.Edit }] });
        Assert.Equal("rebase", (await service.OpenAsync(repo.Root, new(Take: 1), default)).InProgress.Kind);
        service = Service();
        await Do(service, repo, new(GitAction.ContinueOperation));
        Assert.False((await service.OpenAsync(repo.Root, new(Take: 1), default)).InProgress.IsActive);
    }

    [Fact]
    public async Task Cherry_pick_conflict_resolve_continue_then_revert()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var service = Service();
        await repo.Git("switch", "-c", "topic"); await Commit(repo, "tracked.txt", "topic\n", "topic");
        var topic = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("switch", "main"); await Commit(repo, "tracked.txt", "main\n", "main");
        var failed = await service.ExecuteAsync(repo.Root, new(GitAction.CherryPick) { Revision = topic }, null, default);
        Assert.False(failed.Success); Assert.NotNull(failed.Snapshot);
        Assert.Equal("cherry-pick", (await service.OpenAsync(repo.Root, new(Take: 1), default)).InProgress.Kind);
        await Do(service, repo, new(GitAction.WriteConflictResult) { Paths = ["tracked.txt"], Message = "resolved\n" });
        await Do(service, repo, new(GitAction.MarkResolved) { Paths = ["tracked.txt"] });
        await Do(service, repo, new(GitAction.ContinueOperation));
        await Do(service, repo, new(GitAction.Revert) { Revision = "HEAD" });
        Assert.Equal("main\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
    }

    [Theory]
    [InlineData("sha1", "libgit2")] [InlineData("sha256", "Git CLI")]
    public async Task Blame_and_file_history_follow_renames_with_format_fallback(string format, string backend)
    {
        using var repo = new TemporaryRepository(); await repo.Init(format);
        await repo.Git("mv", "tracked.txt", "中文 空白.txt"); await repo.Git("commit", "-m", "rename");
        await Commit(repo, "中文 空白.txt", "base\nsecond\n", "change");
        var history = await Service().GetFileHistoryAsync(repo.Root, "中文 空白.txt", default);
        Assert.Equal(backend, history.Backend); Assert.Equal(2, history.Blame.Count);
        Assert.Equal("base", history.Blame[0].Text); Assert.Equal(1, history.Blame[0].LineNumber);
        Assert.Equal(3, history.History.Count); Assert.Equal("tracked.txt", history.History[^1].Path);
    }
}
