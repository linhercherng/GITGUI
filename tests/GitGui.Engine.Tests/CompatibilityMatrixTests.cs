using System.Diagnostics;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class ReftableAvailableFactAttribute : FactAttribute
{
    public ReftableAvailableFactAttribute()
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("init"); start.ArgumentList.Add("-h");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000)) { process.Kill(true); throw new InvalidOperationException("Git capability probe timed out."); }
        var help = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
        if (!help.Contains("--ref-format", StringComparison.Ordinal)) Skip = "Selected Git has no reftable support (Git 2.40 predates --ref-format). Files/SHA-1 and files/SHA-256 remain required.";
    }
}

public sealed class CompatibilityMatrixTests
{
    [Fact]
    public async Task Selected_Git_matches_exact_CI_pin_and_meets_application_minimum()
    {
        using var repo = new TemporaryRepository();
        var version = (await repo.Git("--version")).Trim();
        Assert.True(GitVersionPolicy.Accepts(version), version);
        var expected = Environment.GetEnvironmentVariable("GITGUI_EXPECTED_GIT_VERSION");
        if (expected is not null) Assert.Equal(expected, version);
        var runner = new GitProcessRunner();
        var actual = await runner.ReadAsync(repo.Root, ["--version"], default);
        Assert.Equal(0, actual.ExitCode); Assert.Equal(version, System.Text.Encoding.UTF8.GetString(actual.Output).Trim());
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public Task Files_format_runs_identical_real_workflow(string objectFormat) => Workflow(objectFormat, "files");

    [ReftableAvailableFact]
    public Task SHA1_reftable_runs_identical_real_workflow() => Workflow("sha1", "reftable");

    [ReftableAvailableFact]
    public Task SHA256_reftable_runs_identical_real_workflow() => Workflow("sha256", "reftable");

    private static async Task Workflow(string objectFormat, string refFormat)
    {
        using var repo = new TemporaryRepository();
        await Init(repo, objectFormat, refFormat);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); var snapshots = new SnapshotManager(runner, catalog);
        var service = new WorkspaceService(runner, new TestSettings(), catalog, snapshots, new TestInteraction(), new LibHistoryReader(), new LibHistoryReader());
        var format = await new CliRepoReader(runner).GetFormatAsync(repo.Root, default);
        Assert.Equal(objectFormat, format.ObjectFormat); Assert.Equal(refFormat, format.RefStorage);
        byte[] binary = [0, 255, 128, 13, 10, 42];
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "base\n");
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "中文 空白.bin"), binary);
        await Success(service, repo.Root, new(GitAction.Stage) { Paths = ["tracked.txt", "中文 空白.bin"] });
        await Success(service, repo.Root, new(GitAction.Commit) { Message = "matrix initial 中文" });
        var opened = await service.OpenAsync(repo.Root, new(Take: 20), default);
        Assert.Equal("main", opened.Status.Branch); Assert.Empty(opened.Status.Files);
        Assert.Equal(objectFormat == "sha256" ? 64 : 40, Assert.Single(opened.Commits).Hash.Length);
        var history = await service.GetFileHistoryAsync(repo.Root, "tracked.txt", default);
        Assert.Equal(objectFormat == "sha1" && refFormat == "files" ? "libgit2" : "Git CLI", history.Backend);
        Assert.Equal("base", Assert.Single(history.Blame).Text);

        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "staged\r\n");
        await Success(service, repo.Root, new(GitAction.Stage) { Paths = ["tracked.txt"] });
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "unstaged\r\n");
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "未追蹤.raw"), binary);
        Assert.Contains("+staged", await service.GetDiffAsync(repo.Root, new("tracked.txt", true), default));
        var beforeHead = await repo.Git("rev-parse", "HEAD");
        var beforeIndex = await repo.Git("ls-files", "--stage", "-z");
        var beforeRefs = await repo.Git("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/tags");
        var snapshot = await snapshots.CreateAsync(repo.Root, "compatibility " + objectFormat + "/" + refFormat, default);
        await repo.Git("reset", "--hard", "HEAD"); await repo.Git("clean", "-f", "--", "未追蹤.raw");
        await repo.Git("branch", "after-snapshot"); await repo.Git("tag", "after-snapshot");
        await repo.Git("gc", "--prune=now");
        var undo = await Success(service, repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = snapshot.Id });
        Assert.Equal(beforeHead, await repo.Git("rev-parse", "HEAD"));
        Assert.Equal(beforeIndex, await repo.Git("ls-files", "--stage", "-z"));
        Assert.Equal(beforeRefs, await repo.Git("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/tags"));
        Assert.Equal("staged\r\nunstaged\r\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.Equal(binary, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "未追蹤.raw")));
        await Success(service, repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = undo.Snapshot!.Id });
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "tracked.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "未追蹤.raw")));

        using var remote = new TemporaryRepository(); using var clone = new TemporaryRepository();
        string[] options = ["init", "--bare", "--initial-branch=main", "--object-format=" + objectFormat];
        if (refFormat == "reftable") options = [.. options, "--ref-format=reftable"];
        await repo.Git([.. options, remote.Root]);
        await repo.Git("remote", "add", "origin", remote.Root);
        await Success(service, repo.Root, new(GitAction.Push) { Remote = "origin" });
        await Success(service, repo.Root, new(GitAction.Fetch) { Remote = "origin" });
        await Success(service, clone.Root, new(GitAction.Clone) { Url = remote.Root });
        Assert.Equal(objectFormat, (await new CliRepoReader(runner).GetFormatAsync(clone.Root, default)).ObjectFormat);
        Assert.Equal(binary, await File.ReadAllBytesAsync(Path.Combine(clone.Root, "中文 空白.bin")));
        await M3Workflow(repo, runner, service, snapshots);
    }

    private static async Task M3Workflow(TemporaryRepository repo, GitProcessRunner runner, WorkspaceService service, SnapshotManager snapshots)
    {
        var tools = new RepositoryToolsService(runner, service, snapshots, new TestInteraction());
        async Task<OperationOutcome> Tool(RepositoryToolOperation operation)
        {
            var result = await tools.ExecuteAsync(repo.Root, operation, null, default);
            Assert.True(result.Success, result.Message); return result;
        }
        using var linked = new TemporaryRepository();
        try
        {
            await Tool(new(RepositoryToolAction.WorktreeAdd) { Path = linked.Root });
            var worktrees = await repo.Git("worktree", "list", "--porcelain");
            Assert.Contains(Path.GetFileName(linked.Root), worktrees); Assert.Contains("detached", worktrees);
            await Tool(new(RepositoryToolAction.WorktreeRemove) { Path = linked.Root });
            Assert.False(Directory.Exists(linked.Root));
        }
        finally { if (File.Exists(Path.Combine(linked.Root, ".git"))) await repo.Git("worktree", "remove", "--force", linked.Root); }

        var good = (await repo.Git("rev-parse", "HEAD")).Trim();
        foreach (var directory in new[] { "included", "excluded" })
        {
            Directory.CreateDirectory(Path.Combine(repo.Root, directory));
            await File.WriteAllTextAsync(Path.Combine(repo.Root, directory, "matrix.txt"), directory + "\n");
        }
        await repo.Git("add", "included", "excluded"); await repo.Git("commit", "-m", "M3 format fixtures");
        var flags = await repo.Git("ls-files", "-t", "-z");
        var sparse = await Tool(new(RepositoryToolAction.SparseSet) { Patterns = ["included"], Cone = true });
        Assert.True(File.Exists(Path.Combine(repo.Root, "included", "matrix.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "excluded", "matrix.txt")));
        Assert.Equal("true", (await repo.Git("config", "--bool", "core.sparseCheckout")).Trim());
        await Success(service, repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = sparse.Snapshot!.Id });
        Assert.Equal("excluded\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "excluded", "matrix.txt")));
        Assert.Equal(flags, await repo.Git("ls-files", "-t", "-z"));
        Assert.Equal("false", (await repo.Git("config", "--bool", "--default", "false", "core.sparseCheckout")).Trim());

        await repo.Git("commit", "--allow-empty", "-m", "M3 bisect bad");
        var bad = (await repo.Git("rev-parse", "HEAD")).Trim();
        await Tool(new(RepositoryToolAction.BisectStart) { GoodRevision = good, Revision = bad });
        Assert.True(File.Exists((await repo.Git("rev-parse", "--path-format=absolute", "--git-path", "BISECT_START")).Trim()));
        await Tool(new(RepositoryToolAction.BisectReset));
        Assert.Equal(bad, (await repo.Git("rev-parse", "HEAD")).Trim());
        Assert.Equal("main", (await repo.Git("branch", "--show-current")).Trim());

        await Tool(new(RepositoryToolAction.LfsInstall));
        Assert.Equal("git-lfs filter-process", (await repo.Git("config", "--local", "filter.lfs.process")).Trim());
        await Tool(new(RepositoryToolAction.LfsTrack) { Patterns = ["*.matrix-lfs"] });
        var attributes = Path.Combine(repo.Root, ".gitattributes");
        Assert.Contains("*.matrix-lfs filter=lfs", await File.ReadAllTextAsync(attributes));
        await Tool(new(RepositoryToolAction.LfsUntrack) { Patterns = ["*.matrix-lfs"] });
        Assert.DoesNotContain("filter=lfs", await File.ReadAllTextAsync(attributes));
    }

    private static async Task Init(TemporaryRepository repo, string objectFormat, string refFormat)
    {
        string[] args = ["init", "--initial-branch=main", "--object-format=" + objectFormat];
        if (refFormat == "reftable") args = [.. args, "--ref-format=reftable"];
        await repo.Git(args);
        await repo.Git("config", "user.name", "GITGUI compatibility"); await repo.Git("config", "user.email", "matrix@example.invalid");
        await repo.Git("config", "commit.gpgsign", "false"); await repo.Git("config", "core.autocrlf", "false");
        await repo.Git("config", "core.hooksPath", Path.Combine(repo.Root, ".git", "hooks"));
    }
    private static async Task<OperationOutcome> Success(WorkspaceService service, string root, GitOperation operation)
    {
        var result = await service.ExecuteAsync(root, operation, null, default); Assert.True(result.Success, result.Message); return result;
    }
}
