using System.Text;
using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class PartialPatchTests
{
    [Theory]
    [InlineData("sample.txt", "add")]
    [InlineData("space 中文.txt", "add")]
    [InlineData("sample.txt", "delete")]
    [InlineData("sample.txt", "replace")]
    [InlineData("sample.txt", "hunk")]
    public async Task Selective_stage_applies_the_selected_lines_to_index_only(string path, string selection)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await CommitFile(repo, path, "one\ntwo\nthree\n");
        var modified = "one\nTWO\nextra\nthree\n";
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), modified);
        var diff = await Diff(repo, path);
        var hunks = UnifiedPatch.Parse(diff);
        var ids = hunks.SelectMany(h => h.Lines).Where(l => selection switch
        {
            "add" => l.Kind == '+' && l.Text == "extra",
            "delete" => l.Kind == '-' && l.Text == "two",
            "replace" => l.Text is "two" or "TWO",
            _ => false
        }).Select(l => l.Id).ToHashSet();
        var patch = UnifiedPatch.Build(diff, selection == "hunk" ? hunks.Select(h => h.Id).ToHashSet() : [], ids);
        await Apply(repo, patch);
        var expected = selection switch { "add" => "one\ntwo\nextra\nthree\n", "delete" => "one\nthree\n", "replace" => "one\nTWO\nthree\n", _ => modified };
        Assert.Equal(expected, await Index(repo, path));
        Assert.Equal(modified, await File.ReadAllTextAsync(Path.Combine(repo.Root, path)));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("replace")]
    [InlineData("hunk")]
    public async Task Reverse_unstage_uses_the_current_index_as_base(string selection)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        const string path = "reverse 中文.txt";
        await CommitFile(repo, path, "one\ntwo\nthree\n");
        const string modified = "one\nTWO\nextra\nthree\n";
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), modified); await repo.Git("add", "--", path);
        var diff = await Diff(repo, path, staged: true);
        var hunks = UnifiedPatch.Parse(diff);
        var lines = hunks.SelectMany(h => h.Lines).Where(l => selection == "add" ? l.Text == "extra" : l.Text is "two" or "TWO").Select(l => l.Id).ToHashSet();
        await Apply(repo, UnifiedPatch.Build(diff, selection == "hunk" ? hunks.Select(h => h.Id).ToHashSet() : [], lines, reverse: true));
        var expected = selection switch { "add" => "one\nTWO\nthree\n", "replace" => "one\ntwo\nextra\nthree\n", _ => "one\ntwo\nthree\n" };
        Assert.Equal(expected, await Index(repo, path));
        Assert.Equal(modified, await File.ReadAllTextAsync(Path.Combine(repo.Root, path)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task New_file_supports_partial_and_full_stage_and_unstage(bool full, bool reverse)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        const string path = "new file 中文.txt";
        const string content = "first\nsecond\nthird\n";
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), content);
        await repo.Git("add", reverse ? "--" : "-N", path);
        var diff = await Diff(repo, path, staged: reverse);
        var hunks = UnifiedPatch.Parse(diff);
        var second = hunks.SelectMany(h => h.Lines).Single(l => l.Text == "second");
        await Apply(repo, UnifiedPatch.Build(diff, full ? hunks.Select(h => h.Id).ToHashSet() : [], full ? [] : new HashSet<int> { second.Id }, reverse));
        if (full && reverse) Assert.False(await InIndex(repo, path));
        else Assert.Equal(reverse ? "first\nthird\n" : full ? content : "second\n", await Index(repo, path));
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(repo.Root, path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleted_file_partial_stage_keeps_unselected_lines_and_full_stage_removes_entry(bool full)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        const string path = "deleted 中文.txt";
        await CommitFile(repo, path, "first\nsecond\nthird\n"); File.Delete(Path.Combine(repo.Root, path));
        var diff = await Diff(repo, path);
        var hunks = UnifiedPatch.Parse(diff);
        var second = hunks.SelectMany(h => h.Lines).Single(l => l.Text == "second");
        await Apply(repo, UnifiedPatch.Build(diff, full ? hunks.Select(h => h.Id).ToHashSet() : [], full ? [] : new HashSet<int> { second.Id }));
        if (full) Assert.False(await InIndex(repo, path)); else Assert.Equal("first\nthird\n", await Index(repo, path));
        Assert.False(File.Exists(Path.Combine(repo.Root, path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleted_file_can_be_partially_or_fully_restored_to_index(bool full)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        const string path = "restore deleted.txt";
        await CommitFile(repo, path, "first\nsecond\nthird\n"); File.Delete(Path.Combine(repo.Root, path)); await repo.Git("add", "--", path);
        var diff = await Diff(repo, path, staged: true); var hunks = UnifiedPatch.Parse(diff);
        var second = hunks.SelectMany(h => h.Lines).Single(l => l.Text == "second");
        await Apply(repo, UnifiedPatch.Build(diff, full ? hunks.Select(h => h.Id).ToHashSet() : [], full ? [] : new HashSet<int> { second.Id }, reverse: true));
        Assert.Equal(full ? "first\nsecond\nthird\n" : "second\n", await Index(repo, path));
        Assert.False(File.Exists(Path.Combine(repo.Root, path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Skipped_prior_hunks_do_not_shift_later_selected_hunks(bool reverse)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        const string path = "many.txt";
        var original = string.Join('\n', Enumerable.Range(1, 60).Select(n => "line " + n)) + "\n";
        await CommitFile(repo, path, original);
        var modified = original.Replace("line 2\n", "line 2\nextra one\nextra two\n", StringComparison.Ordinal).Replace("line 55\n", "LAST\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), modified);
        if (reverse) await repo.Git("add", "--", path);
        var diff = await Diff(repo, path, reverse); var hunks = UnifiedPatch.Parse(diff); Assert.Equal(2, hunks.Count);
        await Apply(repo, UnifiedPatch.Build(diff, new HashSet<int> { hunks[1].Id }, new HashSet<int>(), reverse));
        var expected = reverse ? original.Replace("line 2\n", "line 2\nextra one\nextra two\n", StringComparison.Ordinal) : original.Replace("line 55\n", "LAST\n", StringComparison.Ordinal);
        Assert.Equal(expected, await Index(repo, path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_hunk_preserves_no_newline_markers_in_both_directions(bool reverse)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        const string path = "no-newline.txt";
        await CommitFile(repo, path, "before");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), "after");
        if (reverse) await repo.Git("add", "--", path);
        var diff = await Diff(repo, path, reverse); var hunks = UnifiedPatch.Parse(diff);
        Assert.Contains("\\ No newline at end of file", diff);
        await Apply(repo, UnifiedPatch.Build(diff, hunks.Select(h => h.Id).ToHashSet(), new HashSet<int>(), reverse));
        Assert.Equal(reverse ? "before" : "after", await Index(repo, path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_CRLF_lines_keep_original_bytes(bool reverse)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        const string path = "crlf.txt";
        await CommitFile(repo, path, "one\r\ntwo\r\nthree\r\n");
        const string modified = "one\r\ntwo\r\nadded\r\nthree\r\n";
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), modified);
        if (reverse) await repo.Git("add", "--", path);
        var diff = await Diff(repo, path, reverse); var hunks = UnifiedPatch.Parse(diff);
        var added = hunks.SelectMany(h => h.Lines).Single(l => l.Text == "added\r");
        await Apply(repo, UnifiedPatch.Build(diff, new HashSet<int>(), new HashSet<int> { added.Id }, reverse));
        Assert.Equal(reverse ? "one\r\ntwo\r\nthree\r\n" : modified, await Index(repo, path));
    }

    [Fact]
    public async Task New_file_keeps_executable_mode_from_real_git_diff()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        const string path = "script.sh";
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), "#!/bin/sh\necho hello\n");
        await repo.Git("add", "--", path); await repo.Git("update-index", "--chmod=+x", "--", path);
        var diff = await Diff(repo, path, staged: true);
        Assert.Contains("new file mode 100755", diff);
        await repo.Git("reset", "HEAD", "--", path);
        var hunks = UnifiedPatch.Parse(diff);
        await Apply(repo, UnifiedPatch.Build(diff, hunks.Select(h => h.Id).ToHashSet(), new HashSet<int>()));
        Assert.StartsWith("100755 ", await repo.Git("ls-files", "--stage", "--", path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_file_selected_last_line_preserves_missing_final_newline(bool reverse)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        const string path = "no-newline-new.txt";
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), "first\nsecond\nthird");
        await repo.Git("add", reverse ? "--" : "-N", path);
        var diff = await Diff(repo, path, reverse);
        var third = UnifiedPatch.Parse(diff).SelectMany(h => h.Lines).Single(l => l.Text == "third");
        await Apply(repo, UnifiedPatch.Build(diff, new HashSet<int>(), new HashSet<int> { third.Id }, reverse));
        Assert.Equal(reverse ? "first\nsecond\n" : "third", await Index(repo, path));
    }

    [Fact]
    public async Task Full_reverse_hunk_restores_original_file_mode()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        const string path = "chmod.sh";
        await CommitFile(repo, path, "#!/bin/sh\necho before\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), "#!/bin/sh\necho after\n");
        await repo.Git("add", "--", path); await repo.Git("update-index", "--chmod=+x", "--", path);
        var diff = await Diff(repo, path, staged: true);
        Assert.Contains("old mode 100644", diff); Assert.Contains("new mode 100755", diff);
        await Apply(repo, UnifiedPatch.Build(diff, UnifiedPatch.Parse(diff).Select(h => h.Id).ToHashSet(), new HashSet<int>(), reverse: true));
        Assert.StartsWith("100644 ", await repo.Git("ls-files", "--stage", "--", path));
        Assert.Equal("#!/bin/sh\necho before\n", await Index(repo, path));
    }

    private static async Task CommitFile(TemporaryRepository repo, string path, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(repo.Root, path), content); await repo.Git("add", "--", path); await repo.Git("commit", "-m", "fixture");
    }
    private static async Task<string> Diff(TemporaryRepository repo, string path, bool staged = false)
    {
        var args = staged ? new[] { "diff", "--cached", "--", path } : new[] { "diff", "--", path };
        var result = await new GitProcessRunner().ReadAsync(repo.Root, args, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        return Encoding.UTF8.GetString(result.Output);
    }
    private static Task<string> Index(TemporaryRepository repo, string path) => repo.Git("show", ":" + path);
    private static async Task<bool> InIndex(TemporaryRepository repo, string path)
    {
        var result = await new GitProcessRunner().ReadAsync(repo.Root, ["ls-files", "--error-unmatch", "--", path], CancellationToken.None);
        return result.ExitCode == 0;
    }
    private static async Task Apply(TemporaryRepository repo, string patch)
    {
        var runner = new GitProcessRunner();
        var options = new GitRunOptions { IsReadOnly = false, Trusted = true, Input = Encoding.UTF8.GetBytes(patch), Environment = new Dictionary<string, string?> { ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = Path.Combine(repo.Root, "missing-global-config") } };
        var check = await runner.RunAsync(repo.Root, ["apply", "--cached", "--recount", "--check", "-"], CancellationToken.None, options);
        Assert.True(check.ExitCode == 0, check.Error + "\n" + patch);
        var applied = await runner.RunAsync(repo.Root, ["apply", "--cached", "--recount", "-"], CancellationToken.None, options);
        Assert.True(applied.ExitCode == 0, applied.Error + "\n" + patch);
    }
}
