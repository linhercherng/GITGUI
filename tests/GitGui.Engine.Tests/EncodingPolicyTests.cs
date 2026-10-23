using System.Text;
using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class EncodingPolicyTests
{
    private static WorkspaceService Service(GitProcessRunner runner, MemoryCatalog catalog)
        => new(runner, new TestSettings(), catalog, new SnapshotManager(runner, catalog), new TestInteraction());

    [Fact]
    public async Task Commit_uses_configured_Big5_bytes_and_preserves_config()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("config", "i18n.commitEncoding", "Big5");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "new.txt"), "content\r\n"); await repo.Git("add", "new.txt");
        var runner = new GitProcessRunner(); var service = Service(runner, new());
        var result = await service.ExecuteAsync(repo.Root, new(GitAction.Commit) { Message = "中文" }, null, default);
        Assert.True(result.Success, result.Message);
        var raw = await runner.ReadAsync(repo.Root, ["cat-file", "commit", "HEAD"], default);
        Assert.Contains("encoding Big5\n", Encoding.ASCII.GetString(raw.Output));
        Assert.Equal("中文\n", CommitMessageDecoder.DecodeObject(raw.Output).Message);
        Assert.Equal("Big5", (await repo.Git("config", "i18n.commitEncoding")).Trim());
        var read = await new CommitTextReader(runner).ReadAsync(repo.Root, (await repo.Git("rev-parse", "HEAD")).Trim(), default);
        Assert.Equal("中文", read.Subject); Assert.Equal(CommitEncodingSource.Header, read.Encoding.Source);
    }

    [Theory]
    [InlineData("Big5", "emoji😀")]
    [InlineData("not-an-encoding", "text")]
    [InlineData("", "text")]
    public async Task Unencodable_commit_is_rejected_before_any_write_or_snapshot(string encoding, string message)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("config", "i18n.commitEncoding", encoding);
        var before = await repo.Git("rev-parse", "HEAD"); var index = await repo.Git("ls-files", "--stage", "-z");
        var runner = new GitProcessRunner(); var writes = new List<string>(); runner.CommandExecuted += (_, e) => writes.Add(e.Command);
        var catalog = new MemoryCatalog(); var result = await Service(runner, catalog).ExecuteAsync(repo.Root,
            new(GitAction.Commit) { Message = message }, null, default);
        Assert.False(result.Success); Assert.Null(result.Snapshot); Assert.Contains("i18n.commitEncoding", result.Message);
        Assert.Empty(await catalog.ListAsync(repo.Root, default));
        Assert.Equal(before, await repo.Git("rev-parse", "HEAD")); Assert.Equal(index, await repo.Git("ls-files", "--stage", "-z"));
        Assert.DoesNotContain(writes, c => c.Contains("'write-tree'", StringComparison.Ordinal) || c.Contains("'update-ref'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Text_config_reports_explicit_line_ending_values_without_changing_them()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("config", "core.autocrlf", "input"); await repo.Git("config", "core.safecrlf", "warn"); await repo.Git("config", "core.eol", "crlf");
        var settings = await new TextConfigurationReader(new()).ReadAsync(repo.Root, default);
        Assert.Equal("input", settings.AutoCrlf); Assert.Equal("warn", settings.SafeCrlf); Assert.Equal("crlf", settings.Eol);
        Assert.Equal("input", (await repo.Git("config", "core.autocrlf")).Trim());
    }

    [Fact]
    public async Task Batch_decoder_keeps_each_original_encoding_and_uses_one_process()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); var runner = new GitProcessRunner();
        var tree = (await repo.Git("rev-parse", "HEAD^{tree}")).Trim();
        async Task<string> Commit(byte[] message, string? encoding)
        {
            var header = $"tree {tree}\nauthor Test <a@b> 1 +0000\ncommitter Test <a@b> 1 +0000\n" + (encoding is null ? "" : "encoding " + encoding + "\n") + "\n";
            var result = await runner.RunAsync(repo.Root, ["hash-object", "-t", "commit", "-w", "--stdin"], default,
                new() { Input = Encoding.ASCII.GetBytes(header).Concat(message).ToArray(), IsReadOnly = false, HideOutput = true });
            Assert.Equal(0, result.ExitCode); return Encoding.ASCII.GetString(result.Output).Trim();
        }
        var legacy = await Commit([0xa4, 0xa4, 0xa4, 0xe5, 10], null); var utf8 = await Commit(Encoding.UTF8.GetBytes("測試😀\n"), null);
        var calls = new List<string>(); runner.CommandExecuted += (_, entry) => calls.Add(entry.Command);
        CommitInfo originalLegacy = new(legacy, "中文", "", DateTimeOffset.Now, []);
        CommitInfo originalUtf8 = new(utf8, "測試😀", "", DateTimeOffset.Now, []);
        var commits = await new CommitTextReader(runner).EnrichAsync(repo.Root, [originalLegacy, originalUtf8], default);
        Assert.Equal("中文", commits[0].Subject); Assert.Equal("測試😀", commits[1].Subject);
        Assert.Equal(CommitEncodingSource.Big5Detected, commits[0].MessageEncoding.Source); Assert.Single(calls);
        Assert.Same(originalUtf8, commits[1]); Assert.NotSame(originalLegacy, commits[0]);
    }

    [Fact]
    public async Task Reword_uses_Big5_message_file_and_rejects_unrepresentable_plan_before_snapshot()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var upstream = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("commit", "--allow-empty", "-m", "original"); await repo.Git("config", "i18n.commitEncoding", "Big5");
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); var service = Service(runner, catalog);
        var plan = await service.GetRebasePlanAsync(repo.Root, upstream, default);
        var before = await repo.Git("rev-parse", "HEAD");
        var invalid = await service.ExecuteAsync(repo.Root, new(GitAction.RebaseInteractive) { Revision = upstream,
            RebasePlan = [plan[0] with { Action = RebaseStepKind.Reword, Message = "😀" }] }, null, default);
        Assert.False(invalid.Success); Assert.Null(invalid.Snapshot); Assert.Empty(await catalog.ListAsync(repo.Root, default));
        Assert.Equal(before, await repo.Git("rev-parse", "HEAD"));
        var result = await service.ExecuteAsync(repo.Root, new(GitAction.RebaseInteractive) { Revision = upstream,
            RebasePlan = [plan[0] with { Action = RebaseStepKind.Reword, Message = "中文" }] }, null, default);
        Assert.True(result.Success, result.Message);
        var raw = await runner.ReadAsync(repo.Root, ["cat-file", "commit", "HEAD"], default);
        Assert.Equal("中文\n", CommitMessageDecoder.DecodeObject(raw.Output).Message);
        Assert.Contains("encoding Big5\n", Encoding.ASCII.GetString(raw.Output));
        Assert.Equal("Big5", (await repo.Git("config", "i18n.commitEncoding")).Trim());
    }

    [Fact]
    public async Task Cold_historical_diff_and_preview_support_Big5_without_a_details_cache()
    {
        using var repo = new TemporaryRepository(); await repo.Init(); await repo.Git("config", "i18n.commitEncoding", "Big5");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "changed\n");
        var runner = new GitProcessRunner();
        var committed = await runner.RunAsync(repo.Root, ["commit", "--all", "--file=-"], default,
            new() { IsReadOnly = false, Trusted = true, Input = CommitMessageDecoder.Encode("中文", "Big5") });
        Assert.Equal(0, committed.ExitCode);
        var oid = (await repo.Git("rev-parse", "HEAD")).Trim();
        await repo.Git("config", "i18n.logOutputEncoding", "Big5");
        var diff = await Service(new(), new()).GetDiffAsync(repo.Root, new("tracked.txt", Commit: oid), default);
        Assert.Contains("+changed", diff);
        var preview = await Service(new(), new()).GetPreviewAsync(repo.Root, new("tracked.txt", Commit: oid), default);
        Assert.Equal(5, preview.BeforeSize); Assert.Equal(8, preview.AfterSize);
    }
}
