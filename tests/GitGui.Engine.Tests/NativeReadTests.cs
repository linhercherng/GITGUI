using System.Text;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class NativeReadTests
{
    [Fact]
    public async Task Native_reads_reuse_fresh_configuration_for_equivalent_repository_paths()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        const string changed = "changed through equivalent root\n";
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), changed);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
        await service.OpenAsync(repo.Root, new(), default);
        var commands = new List<CommandEntry>(); runner.CommandExecuted += (_, entry) => commands.Add(entry);
        var equivalent = Path.Combine(repo.Root, ".");
        Assert.Contains("+changed through equivalent root", await service.GetDiffAsync(equivalent, new("tracked.txt"), default));
        Assert.Equal(Encoding.UTF8.GetByteCount(changed), (await service.GetPreviewAsync(equivalent, new("tracked.txt"), default)).AfterSize);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task Native_attribute_guard_reads_index_fallback_after_worktree_attribute_deletion()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var attributes = Path.Combine(repo.Root, ".gitattributes");
        await File.WriteAllTextAsync(attributes, "tracked.txt filter=unsafe\n");
        await repo.Git("add", ".gitattributes"); await repo.Git("commit", "-m", "indexed attributes");
        File.Delete(attributes);
        using var git = new LibGit2Sharp.Repository(repo.Root);
        var context = new NativeReadContext(git.Config.Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value)).ToArray(),
            new GitRuntime().ResolveExecutable(null), null, null, false);
        using var native = new LibHistoryReader();
        Assert.Null(await native.TryReadPreviewAsync(repo.Root, new("tracked.txt"), context, default));
    }

    [Theory]
    [InlineData("\v")]
    [InlineData("\f")]
    public async Task Native_attribute_guard_handles_Git_whitespace_separators(string separator)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".gitattributes"), "tracked.txt" + separator + "filter=unsafe\n");
        using var git = new LibGit2Sharp.Repository(repo.Root);
        var context = new NativeReadContext(git.Config.Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value)).ToArray(),
            new GitRuntime().ResolveExecutable(null), null, null, false);
        using var native = new LibHistoryReader();
        Assert.Null(await native.TryReadPreviewAsync(repo.Root, new("tracked.txt"), context, default));
    }

    [Fact]
    public async Task Native_deletion_patch_stages_only_index_and_cancelled_reads_do_not_start_Git()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        File.Delete(Path.Combine(repo.Root, "tracked.txt"));
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
        await service.OpenAsync(repo.Root, new(), default);
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        var diff = await service.GetDiffAsync(repo.Root, new("tracked.txt"), default);
        Assert.Empty(commands); Assert.Contains("deleted file mode", diff);
        await Apply(repo.Root, runner, UnifiedPatch.Build(diff, UnifiedPatch.Parse(diff).Select(h => h.Id).ToHashSet(), new HashSet<int>()));
        Assert.Empty((await repo.Git("ls-files", "--", "tracked.txt")).Trim());
        Assert.False(File.Exists(Path.Combine(repo.Root, "tracked.txt")));
        commands.Clear(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetDiffAsync(repo.Root, new("tracked.txt"), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetPreviewAsync(repo.Root, new("tracked.txt"), cancellation.Token));
        Assert.Empty(commands);
    }

    [Fact]
    public async Task Legacy_non_UTF8_file_diff_uses_byte_preserving_CLI_fallback()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        byte[] original = [0xA4, 0xA4, 0xA4, 0xE5, 10]; byte[] changed = [0xA4, 0xA4, 0xA4, 0xE5, 0xA6, 0x72, 10];
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "legacy.txt"), original);
        await repo.Git("add", "legacy.txt"); await repo.Git("commit", "-m", "legacy file");
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "legacy.txt"), changed);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
        await service.OpenAsync(repo.Root, new(), default);
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        Assert.Contains("Non-UTF-8", await service.GetDiffAsync(repo.Root, new("legacy.txt"), default));
        Assert.Contains(commands, command => command.Contains(" diff ", StringComparison.Ordinal));
        Assert.Equal(changed, await File.ReadAllBytesAsync(Path.Combine(repo.Root, "legacy.txt")));
    }

    [Fact]
    public async Task Native_route_rechecks_included_configuration_and_attribute_files_after_open()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var include = Path.Combine(repo.Root, ".git", "native-read-include");
        var attributes = Path.Combine(repo.Root, ".git", "external-attributes");
        await File.WriteAllTextAsync(include, "[core]\n safecrlf = false\n");
        await repo.Git("config", "include.path", include);
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "changed\n");
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
        await service.OpenAsync(repo.Root, new(), default);
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        Assert.Contains("+changed", await service.GetDiffAsync(repo.Root, new("tracked.txt"), default));
        Assert.Empty(commands);
        await File.WriteAllTextAsync(attributes, "tracked.txt -diff\n");
        await File.WriteAllTextAsync(include, "[core]\n attributesFile = " + attributes.Replace('\\', '/') + "\n");
        Assert.Contains("Binary files", await service.GetDiffAsync(repo.Root, new("tracked.txt"), default));
        Assert.Contains(commands, command => command.Contains("--show-origin", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains(" diff ", StringComparison.Ordinal));
        await File.WriteAllTextAsync(include, "[core]\n safecrlf = false\n");
        await service.OpenAsync(repo.Root, new(), default); commands.Clear();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".git", "info", "attributes"), "tracked.txt diff=special\n");
        Assert.Contains("+changed", await service.GetDiffAsync(repo.Root, new("tracked.txt"), default));
        Assert.Contains(commands, command => command.Contains(" diff ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sha256")]
    [InlineData("shallow")]
    [InlineData("replace")]
    public async Task Unsupported_repository_semantics_keep_the_CLI_diff_route(string kind)
    {
        using var repo = new TemporaryRepository(); await repo.Init(kind == "sha256" ? "sha256" : "sha1");
        var oid = (await repo.Git("rev-parse", "HEAD")).Trim();
        if (kind == "reftable") await repo.Git("refs", "migrate", "--ref-format=reftable");
        if (kind == "shallow") await File.WriteAllTextAsync(Path.Combine(repo.Root, ".git", "shallow"), oid + "\n");
        if (kind == "replace")
        {
            var replacement = (await repo.Git("commit-tree", "HEAD^{tree}", "-m", "replacement")).Trim();
            await repo.Git("replace", oid, replacement);
        }
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "fallback edit\n");
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
        await service.OpenAsync(repo.Root, new(), default);
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        Assert.Contains("+fallback edit", await service.GetDiffAsync(repo.Root, new("tracked.txt"), default));
        Assert.Contains(commands, command => command.Contains(" diff ", StringComparison.Ordinal));
    }

    [ReftableAvailableFact]
    public Task Reftable_diff_uses_selected_CLI() => Unsupported_repository_semantics_keep_the_CLI_diff_route("reftable");

    [Fact]
    public async Task Active_filter_in_untrusted_repository_is_disabled_on_CLI_fallback()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var marker = Path.Combine(repo.Root, ".git", "filter-executed");
        await repo.Git("config", "filter.unsafe.clean", "echo executed > '" + marker.Replace('\\', '/') + "'; cat");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".gitattributes"), "tracked.txt filter=unsafe\n");
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "safe edit\n");
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(false), catalog, new(runner, catalog), new TestInteraction(), native);
        await service.OpenAsync(repo.Root, new(), default);
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        Assert.Contains("+safe edit", await service.GetDiffAsync(repo.Root, new("tracked.txt"), default));
        Assert.False(File.Exists(marker));
        Assert.Contains(commands, command => command.Contains("filter.unsafe.clean=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("new 中文.txt", "new text\n")]
    [InlineData("empty.txt", "")]
    public async Task Untracked_native_diff_preserves_new_and_empty_file_headers(string path, string text)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), text);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
        await service.OpenAsync(repo.Root, new(), default);
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        var diff = await service.GetDiffAsync(repo.Root, new(path), default);
        Assert.Empty(commands); Assert.Contains("new file mode", diff);
        if (text.Length > 0)
        {
            await Apply(repo.Root, runner, UnifiedPatch.Build(diff, UnifiedPatch.Parse(diff).Select(h => h.Id).ToHashSet(), new HashSet<int>()));
            Assert.Equal(text, await repo.Git("show", ":" + path));
        }
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public async Task Tracked_native_diff_and_preview_do_not_start_Git_and_selected_hunks_roundtrip(string newline, bool autoCrlf)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("config", "core.autocrlf", autoCrlf ? "true" : "false");
        var original = string.Join(newline, Enumerable.Range(1, 24).Select(n => "line " + n)) + newline;
        var indexed = autoCrlf ? original.Replace("\r\n", "\n") : original;
        var indexNewline = autoCrlf ? "\n" : newline;
        const string path = "sample.txt";
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), original);
        await repo.Git("add", path); await repo.Git("commit", "-m", "sample");
        var modified = original.Replace("line 2" + newline, "changed two" + newline).Replace("line 22" + newline, "changed twenty two" + newline);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), modified);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
        await service.OpenAsync(repo.Root, new(), default);
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        var indexBeforeRead = await File.ReadAllBytesAsync(Path.Combine(repo.Root, ".git", "index"));
        var diff = await service.GetDiffAsync(repo.Root, new(path), default);
        var preview = await service.GetPreviewAsync(repo.Root, new(path), default);
        Assert.Empty(commands);
        Assert.Equal(indexBeforeRead, await File.ReadAllBytesAsync(Path.Combine(repo.Root, ".git", "index")));
        Assert.Equal(Encoding.UTF8.GetByteCount(indexed), preview.BeforeSize);
        Assert.Equal(Encoding.UTF8.GetByteCount(modified), preview.AfterSize);
        Assert.Equal(await repo.Git("-c", "core.quotepath=false", "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--unified=3", "--", path), diff);
        var hunks = UnifiedPatch.Parse(diff); Assert.Equal(2, hunks.Count);
        await Apply(repo.Root, runner, UnifiedPatch.Build(diff, new HashSet<int> { hunks[0].Id }, new HashSet<int>()));
        Assert.Equal(indexed.Replace("line 2" + indexNewline, "changed two" + indexNewline), await repo.Git("show", ":" + path));
        var staged = await service.GetDiffAsync(repo.Root, new(path, Staged: true), default);
        await Apply(repo.Root, runner, UnifiedPatch.Build(staged, UnifiedPatch.Parse(staged).Select(h => h.Id).ToHashSet(), new HashSet<int>(), reverse: true));
        Assert.Equal(indexed, await repo.Git("show", ":" + path));
        Assert.Equal(modified, await File.ReadAllTextAsync(Path.Combine(repo.Root, path)));
    }

    [Fact]
    public async Task Native_preview_reads_binary_bytes_and_historical_diff_uses_first_parent()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("switch", "-c", "side");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "side.txt"), "side\n");
        await repo.Git("add", "side.txt"); await repo.Git("commit", "-m", "side");
        await repo.Git("switch", "main"); await repo.Git("commit", "--allow-empty", "-m", "main");
        await repo.Git("merge", "--no-ff", "--no-edit", "side");
        byte[] before = [0, 1, 2, 255]; byte[] after = [0, 4, 5, 254];
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "image.png"), before); await repo.Git("add", "image.png");
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "image.png"), after);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
        await service.OpenAsync(repo.Root, new(), default);
        var oid = (await repo.Git("rev-parse", "HEAD")).Trim();
        var commands = new List<string>(); runner.CommandExecuted += (_, e) => commands.Add(e.Command);
        var preview = await service.GetPreviewAsync(repo.Root, new("image.png"), default);
        Assert.Equal(before, preview.BeforeImage); Assert.Equal(after, preview.AfterImage);
        Assert.Contains("Binary files", await service.GetDiffAsync(repo.Root, new("image.png"), default));
        Assert.Contains("+side", await service.GetDiffAsync(repo.Root, new("side.txt", Commit: oid), default));
        Assert.Empty(commands);
    }

    private static async Task Apply(string root, GitProcessRunner runner, string patch)
    {
        var options = new GitRunOptions { Trusted = true, IsReadOnly = false, Input = Encoding.UTF8.GetBytes(patch) };
        var check = await runner.RunAsync(root, ["apply", "--cached", "--recount", "--check", "-"], default, options);
        Assert.True(check.ExitCode == 0, check.Error + "\n" + patch);
        var result = await runner.RunAsync(root, ["apply", "--cached", "--recount", "-"], default, options);
        Assert.True(result.ExitCode == 0, result.Error + "\n" + patch);
    }
}
