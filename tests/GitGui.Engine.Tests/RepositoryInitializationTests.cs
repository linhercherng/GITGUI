using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public class RepositoryInitializationTests
{
    private static WorkspaceService Service()
    {
        var runner = new GitProcessRunner();
        var catalog = new MemoryCatalog();
        return new(runner, new TestSettings(), catalog, new SnapshotManager(runner, catalog), new TestInteraction());
    }

    [Fact]
    public async Task Existing_project_can_be_initialized_staged_and_committed_without_changing_files()
    {
        using var folder = new TemporaryRepository();
        Directory.CreateDirectory(Path.Combine(folder.Root, "中文 資料夾"));
        var files = new Dictionary<string, byte[]>
        {
            ["README.md"] = "# 我的專案\r\n"u8.ToArray(),
            ["中文 資料夾/image.bin"] = [0, 255, 128, 13, 10],
            [".gitignore"] = ".env\n"u8.ToArray(),
            [".env"] = "EXAMPLE_ONLY=keep-local\n"u8.ToArray()
        };
        foreach (var (path, content) in files) await File.WriteAllBytesAsync(Path.Combine(folder.Root, path), content);
        var service = Service();
        var initialized = await service.ExecuteAsync(folder.Root, new(GitAction.Init), null, default);
        Assert.True(initialized.Success, initialized.Message);
        var state = await service.OpenAsync(folder.Root, new(), default);
        Assert.Equal("main", state.Status.Branch);
        Assert.Empty(state.Commits);
        Assert.DoesNotContain(state.Status.Files, f => f.Path == ".env");
        await folder.Git("config", "user.name", "Local Test");
        await folder.Git("config", "user.email", "local@example.invalid");
        await folder.Git("config", "commit.gpgsign", "false");
        var stage = await service.ExecuteAsync(folder.Root, new(GitAction.Stage) { Paths = state.Status.Files.Select(f => f.Path).ToArray() }, null, default);
        Assert.True(stage.Success, stage.Message);
        var commit = await service.ExecuteAsync(folder.Root, new(GitAction.Commit) { Message = "首次備份" }, null, default);
        Assert.True(commit.Success, commit.Message);
        Assert.Equal("1", (await folder.Git("rev-list", "--count", "HEAD")).Trim());
        Assert.Empty((await folder.Git("status", "--porcelain")).Trim());
        Assert.Empty((await folder.Git("remote")).Trim());
        Assert.DoesNotContain(".env", await folder.Git("ls-files"));
        foreach (var (path, content) in files) Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(folder.Root, path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_repository_is_not_reinitialized_even_without_commits(bool committed)
    {
        using var repo = new TemporaryRepository();
        await repo.Init(commit: committed);
        var head = await File.ReadAllBytesAsync(Path.Combine(repo.Root, ".git", "HEAD"));
        var config = await File.ReadAllBytesAsync(Path.Combine(repo.Root, ".git", "config"));
        var result = await Service().ExecuteAsync(repo.Root, new(GitAction.Init) { Name = "different" }, null, default);
        Assert.False(result.Success);
        Assert.Equal(head, await File.ReadAllBytesAsync(Path.Combine(repo.Root, ".git", "HEAD")));
        Assert.Equal(config, await File.ReadAllBytesAsync(Path.Combine(repo.Root, ".git", "config")));
    }

    [Fact]
    public async Task Existing_git_file_and_nested_repository_are_rejected_without_new_metadata()
    {
        using var folder = new TemporaryRepository();
        await File.WriteAllTextAsync(Path.Combine(folder.Root, ".git"), "gitdir: missing-target\n");
        Assert.False((await Service().ExecuteAsync(folder.Root, new(GitAction.Init), null, default)).Success);
        Assert.Equal("gitdir: missing-target\n", await File.ReadAllTextAsync(Path.Combine(folder.Root, ".git")));
        using var parent = new TemporaryRepository();
        await parent.Init();
        var nested = Path.Combine(parent.Root, "existing project");
        Directory.CreateDirectory(nested);
        Assert.False((await Service().ExecuteAsync(nested, new(GitAction.Init), null, default)).Success);
        await File.WriteAllTextAsync(Path.Combine(nested, "keep.txt"), "keep");
        Assert.False((await Service().ExecuteAsync(nested, new(GitAction.Init), null, default)).Success);
        Assert.False(Directory.Exists(Path.Combine(nested, ".git")));
    }

    [Fact]
    public async Task Clone_still_rejects_nonempty_folder_and_keeps_original_file()
    {
        using var folder = new TemporaryRepository();
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "keep.txt"), "keep");
        var result = await Service().ExecuteAsync(folder.Root, new(GitAction.Clone) { Url = "https://example.invalid/project.git" }, null, default);
        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(folder.Root, ".git")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(folder.Root, "keep.txt")));
    }

    [Fact]
    public async Task Initialization_can_set_only_this_repositorys_commit_identity()
    {
        using var folder = new TemporaryRepository();
        var service = Service();
        var result = await service.ExecuteAsync(folder.Root, new(GitAction.Init)
        {
            InitAuthorName = " 新手 使用者 ", InitAuthorEmail = " beginner@example.invalid "
        }, null, default);
        Assert.True(result.Success, result.Message);
        Assert.Equal("新手 使用者", (await folder.Git("config", "--local", "--get", "user.name")).Trim());
        Assert.Equal("beginner@example.invalid", (await folder.Git("config", "--local", "--get", "user.email")).Trim());
        await folder.Git("config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "first.txt"), "first");
        Assert.True((await service.ExecuteAsync(folder.Root, new(GitAction.Stage) { Paths = ["first.txt"] }, null, default)).Success);
        var commit = await service.ExecuteAsync(folder.Root, new(GitAction.Commit) { Message = "first" }, null, default);
        Assert.True(commit.Success, commit.Message);
        Assert.Equal("新手 使用者 <beginner@example.invalid>", (await folder.Git("log", "-1", "--format=%an <%ae>")).Trim());
    }

    [Theory]
    [InlineData("Only name", null)]
    [InlineData(null, "only@example.invalid")]
    [InlineData("Bad\nName", "email@example.invalid")]
    [InlineData("Name", "email\r@example.invalid")]
    public async Task Invalid_partial_identity_is_rejected_before_creating_git_metadata(string? name, string? email)
    {
        using var folder = new TemporaryRepository();
        var result = await Service().ExecuteAsync(folder.Root, new(GitAction.Init) { InitAuthorName = name, InitAuthorEmail = email }, null, default);
        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(folder.Root, ".git")));
    }

    [Fact]
    public async Task Bare_repository_is_not_reinitialized()
    {
        using var folder = new TemporaryRepository();
        await folder.Git("init", "--bare", "--initial-branch=main");
        var head = await File.ReadAllBytesAsync(Path.Combine(folder.Root, "HEAD"));
        Assert.False((await Service().ExecuteAsync(folder.Root, new(GitAction.Init), null, default)).Success);
        Assert.False(Directory.Exists(Path.Combine(folder.Root, ".git")));
        Assert.Equal(head, await File.ReadAllBytesAsync(Path.Combine(folder.Root, "HEAD")));
    }
}
