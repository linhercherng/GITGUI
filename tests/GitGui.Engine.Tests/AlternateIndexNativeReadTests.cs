using System.Collections.Concurrent;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using Xunit;

namespace GitGui.Engine.Tests;

[Collection("NativeRefEnvironment")]
public sealed class AlternateIndexNativeReadTests
{
    [Fact]
    public void Native_view_guard_distinguishes_removed_overrides_from_empty_and_nonempty_values()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!);
        start.Environment.Clear();
        Assert.True(GitReadEnvironment.SupportsNativeRepositoryView(start));
        start.Environment["GIT_INDEX_FILE"] = null;
        Assert.True(GitReadEnvironment.SupportsNativeRepositoryView(start));
        start.Environment["GIT_INDEX_FILE"] = "";
        Assert.False(GitReadEnvironment.SupportsNativeRepositoryView(start));
        start.Environment["GIT_INDEX_FILE"] = "alternate-index";
        Assert.False(GitReadEnvironment.SupportsNativeRepositoryView(start));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staged_reads_use_selected_Git_alternate_index_after_open(bool preview)
    {
        var previous = Environment.GetEnvironmentVariable("GIT_INDEX_FILE");
        using var repo = new TemporaryRepository();
        try
        {
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", null);
            await repo.Init();
            var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
            var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
            await service.OpenAsync(repo.Root, new(), default);
            var index = Path.Combine(repo.Root, ".git", "index");
            var originalIndex = await File.ReadAllBytesAsync(index);
            var alternate = Path.Combine(repo.Root, ".git", "alternate index 中文");
            File.Copy(index, alternate);
            await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "alternate staged content\n");
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", alternate);
            await repo.Git("add", "--", "tracked.txt");
            var alternateIndex = await File.ReadAllBytesAsync(alternate);
            var commands = new ConcurrentQueue<CommandEntry>(); runner.CommandExecuted += (_, entry) => commands.Enqueue(entry);
            var request = new DiffRequest("tracked.txt", Staged: true);

            if (preview)
            {
                var expectedBefore = long.Parse((await repo.Git("cat-file", "-s", "HEAD:tracked.txt")).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var expectedAfter = long.Parse((await repo.Git("cat-file", "-s", ":tracked.txt")).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var actual = await service.GetPreviewAsync(repo.Root, request, default);
                Assert.Equal(expectedBefore, actual.BeforeSize);
                Assert.Equal(expectedAfter, actual.AfterSize);
                Assert.Contains(commands, entry => entry.Arguments.FirstOrDefault() == "cat-file");
            }
            else
            {
                var expected = await repo.Git("-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "--unified=3", "--cached", "--", "tracked.txt");
                Assert.Contains("+alternate staged content", expected);
                Assert.Equal(expected, await service.GetDiffAsync(repo.Root, request, default));
                Assert.Contains(commands, entry => entry.Arguments.FirstOrDefault() == "diff");
            }
            Assert.Equal(originalIndex, await File.ReadAllBytesAsync(index));
            Assert.Equal(alternateIndex, await File.ReadAllBytesAsync(alternate));
        }
        finally { Environment.SetEnvironmentVariable("GIT_INDEX_FILE", previous); }
    }
}
