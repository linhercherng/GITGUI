using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

internal sealed class TestInteraction : IWorkspaceInteraction
{
    public bool Accept { get; set; } = true;
    public List<Confirmation> Confirmations { get; } = [];
    public Task<ConfirmationAnswer> ConfirmAsync(Confirmation confirmation, CancellationToken ct)
    {
        Confirmations.Add(confirmation);
        return Task.FromResult(new ConfirmationAnswer(Accept, confirmation.RequiredText));
    }
    public Task<string?> CredentialAsync(string prompt, CancellationToken ct) => Task.FromResult<string?>(null);
}
internal sealed class TestSettings(bool trusted = true) : IRepositorySettings
{
    private bool trusted = trusted;
    public Task<bool> IsTrustedAsync(string root, CancellationToken ct) => Task.FromResult(trusted);
    public Task TrustAsync(string root, CancellationToken ct) { trusted = true; return Task.CompletedTask; }
}

public class WorkspaceTests
{
    private static WorkspaceService Service(TestInteraction? interaction = null, TestSettings? settings = null)
    {
        var runner = new GitProcessRunner();
        var catalog = new MemoryCatalog();
        return new(runner, settings ?? new(), catalog, new SnapshotManager(runner, catalog), interaction ?? new());
    }

    [Fact]
    public async Task Commit_amend_undo_and_redo_preserve_history_and_staging()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        var service = Service();
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "second\n");
        Assert.True((await service.ExecuteAsync(repo.Root, new(GitAction.Stage) { Paths = ["tracked.txt"] }, null, default)).Success);
        var commit = await service.ExecuteAsync(repo.Root, new(GitAction.Commit) { Message = "second" }, null, default);
        Assert.True(commit.Success, commit.Message);
        var original = (await repo.Git("rev-parse", "HEAD")).Trim();
        var amend = await service.ExecuteAsync(repo.Root, new(GitAction.Commit) { Message = "amended", Amend = true }, null, default);
        Assert.True(amend.Success, amend.Message);
        Assert.NotNull(amend.Snapshot);
        var amended = (await repo.Git("rev-parse", "HEAD")).Trim();
        Assert.NotEqual(original, amended);
        var undo = await service.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = amend.Snapshot.Id }, null, default);
        Assert.True(undo.Success, undo.Message);
        Assert.Equal(original, (await repo.Git("rev-parse", "HEAD")).Trim());
        var redo = await service.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = undo.Snapshot!.Id }, null, default);
        Assert.True(redo.Success, redo.Message);
        Assert.Equal(amended, (await repo.Git("rev-parse", "HEAD")).Trim());
    }

    [Fact]
    public async Task Partial_stage_and_reverse_unstage_change_only_selected_lines()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "base\nfirst\nsecond\n");
        var service = Service();
        var diff = await service.GetDiffAsync(repo.Root, new("tracked.txt"), default);
        var line = Assert.Single(UnifiedPatch.Parse(diff)).Lines.Single(l => l.Text == "first");
        var patch = UnifiedPatch.Build(diff, new HashSet<int>(), new HashSet<int> { line.Id });
        var stage = await service.ExecuteAsync(repo.Root, new(GitAction.StagePatch) { Patch = patch }, null, default);
        Assert.True(stage.Success, stage.Message);
        Assert.Equal("base\nfirst\n", await repo.Git("show", ":tracked.txt"));
        diff = await service.GetDiffAsync(repo.Root, new("tracked.txt", true), default);
        line = Assert.Single(UnifiedPatch.Parse(diff)).Lines.Single(l => l.Text == "first");
        patch = UnifiedPatch.Build(diff, new HashSet<int>(), new HashSet<int> { line.Id }, reverse: true);
        var unstage = await service.ExecuteAsync(repo.Root, new(GitAction.UnstagePatch) { Patch = patch }, null, default);
        Assert.True(unstage.Success, unstage.Message);
        Assert.Equal("base\n", await repo.Git("show", ":tracked.txt"));
        Assert.Equal("base\nfirst\nsecond\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
    }

    [Fact]
    public async Task Unborn_init_new_file_staging_unstaging_and_first_commit_work()
    {
        using var repo = new TemporaryRepository();
        var service = Service();
        var init = await service.ExecuteAsync(repo.Root, new(GitAction.Init) { Name = "main" }, null, default);
        Assert.True(init.Success, init.Message);
        await repo.Git("config", "user.name", "Test");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "--中文.txt"), "content\n");
        Assert.True((await service.ExecuteAsync(repo.Root, new(GitAction.Stage) { Paths = ["--中文.txt"] }, null, default)).Success);
        Assert.True((await service.ExecuteAsync(repo.Root, new(GitAction.Unstage) { Paths = ["--中文.txt"] }, null, default)).Success);
        Assert.Empty((await repo.Git("ls-files")).Trim());
        await service.ExecuteAsync(repo.Root, new(GitAction.Stage) { Paths = ["--中文.txt"] }, null, default);
        var commit = await service.ExecuteAsync(repo.Root, new(GitAction.Commit) { Message = "first" }, null, default);
        Assert.True(commit.Success, commit.Message);
        var details = await service.GetCommitAsync(repo.Root, "HEAD", default);
        Assert.Equal("--中文.txt", Assert.Single(details.Files).Path);
        Assert.Contains("+content", await service.GetDiffAsync(repo.Root, new("--中文.txt", Commit: details.Commit.Hash), default));
    }

    [Fact]
    public async Task Declined_trust_and_high_risk_confirmation_never_modify_files()
    {
        using var repo = new TemporaryRepository();
        await repo.Init();
        var interaction = new TestInteraction { Accept = false };
        var service = Service(interaction, new(false));
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "keep\n");
        var declined = await service.ExecuteAsync(repo.Root, new(GitAction.Discard) { Paths = ["tracked.txt"] }, null, default);
        Assert.True(declined.Cancelled);
        Assert.Contains("keep", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.Empty((await repo.Git("for-each-ref", "refs/gitgui")).Trim());
        var pathEscape = await Service().ExecuteAsync(repo.Root, new(GitAction.Stage) { Paths = ["../outside"] }, null, default);
        Assert.False(pathEscape.Success);
    }
}
