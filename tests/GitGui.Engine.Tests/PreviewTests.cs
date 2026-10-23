using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public class PreviewTests
{
    [Fact]
    public async Task Image_preview_distinguishes_HEAD_index_worktree_and_commit_parent_bytes()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var path = Path.Combine(repo.Root, "sample.png");
        byte[] original = [137, 80, 78, 71, 0, 1];
        byte[] staged = [137, 80, 78, 71, 0, 2, 3];
        byte[] unstaged = [137, 80, 78, 71, 0, 4, 5, 6];
        await File.WriteAllBytesAsync(path, original); await repo.Git("add", "--", "sample.png");
        await repo.Git("commit", "-m", "image");
        await File.WriteAllBytesAsync(path, staged); await repo.Git("add", "--", "sample.png");
        await File.WriteAllBytesAsync(path, unstaged);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var index = await service.GetPreviewAsync(repo.Root, new("sample.png", Staged: true), default);
        Assert.Equal(original, index.BeforeImage); Assert.Equal(staged, index.AfterImage);
        Assert.Equal(original.Length, index.BeforeSize); Assert.Equal(staged.Length, index.AfterSize);
        var worktree = await service.GetPreviewAsync(repo.Root, new("sample.png"), default);
        Assert.Equal(staged, worktree.BeforeImage); Assert.Equal(unstaged, worktree.AfterImage);
        await repo.Git("commit", "-m", "second image");
        var commit = await service.GetPreviewAsync(repo.Root, new("sample.png", Commit: "HEAD"), default);
        Assert.Equal(original, commit.BeforeImage); Assert.Equal(staged, commit.AfterImage);
    }

    [Fact]
    public async Task Legacy_encoded_diff_never_offers_a_reencoded_partial_patch()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "tracked.txt"), [0xa4, 0xa4, 10]);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var diff = await service.GetDiffAsync(repo.Root, new("tracked.txt"), default);
        Assert.Contains("Non-UTF-8", diff); Assert.Empty(UnifiedPatch.Parse(diff));
        var staged = await service.ExecuteAsync(repo.Root, new(GitAction.Stage) { Paths = ["tracked.txt"] }, null, default);
        Assert.True(staged.Success, staged.Message);
        var blob = await runner.ReadAsync(repo.Root, ["show", ":tracked.txt"], default);
        Assert.Equal(new byte[] { 0xa4, 0xa4, 10 }, blob.Output);
    }
}
