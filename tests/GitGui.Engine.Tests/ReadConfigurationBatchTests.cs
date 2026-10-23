using System.Collections.Concurrent;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Engine.Lib;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class ReadConfigurationBatchTests
{
    [Fact]
    public async Task An_unused_non_UTF8_configuration_value_does_not_prevent_opening_the_repository()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        byte[] legacy = [.. System.Text.Encoding.ASCII.GetBytes("\n[custom]\n legacy = "), 0xff, 0x0a];
        await using (var config = File.Open(Path.Combine(repo.Root, ".git", "config"), FileMode.Append))
            await config.WriteAsync(legacy);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var state = await service.OpenAsync(repo.Root, new(), default);
        var expectedRoot = Path.GetFullPath((await repo.Git("rev-parse", "--show-toplevel")).Trim());
        Assert.Equal(expectedRoot, state.Root); Assert.Single(state.Commits);
    }

    [Theory]
    [InlineData("XDG_CONFIG_HOME")]
    [InlineData("xdg_config_home")]
    [InlineData("GIT_CONFIG_GLOBAL")]
    [InlineData("GIT_CONFIG_COUNT")]
    public void Cached_read_configuration_is_invalidated_when_child_configuration_environment_changes(string key)
    {
        using var repo = new TemporaryRepository();
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!);
        var (snapshot, isCurrent) = EmptySnapshot(repo.Root, start);
        Assert.True((bool)isCurrent.Invoke(snapshot, [start])!);
        start.Environment[key] = key == "GIT_CONFIG_COUNT" ? "1" : Path.Combine(repo.Root, "new-source");
        Assert.False((bool)isCurrent.Invoke(snapshot, [start])!);
    }

    [Fact]
    public void Newly_created_local_configuration_invalidates_an_empty_snapshot()
    {
        using var repo = new TemporaryRepository();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".git"));
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!);
        var (snapshot, isCurrent) = EmptySnapshot(repo.Root, start);
        File.WriteAllText(Path.Combine(repo.Root, ".git", "config"), "[core]\n attributesFile = special-attributes\n");
        Assert.False((bool)isCurrent.Invoke(snapshot, [start])!);
    }

    private static (object Snapshot, System.Reflection.MethodInfo IsCurrent) EmptySnapshot(string root, System.Diagnostics.ProcessStartInfo start)
    {
        var type = typeof(WorkspaceService).Assembly.GetType("GitGui.Engine.Cli.RepositoryReadConfiguration", true)!;
        return (Activator.CreateInstance(type, root, start, Array.Empty<byte>())!, type.GetMethod("IsCurrent")!);
    }

    [Fact]
    public async Task Noncacheable_legacy_configuration_is_resolved_once_before_CLI_ref_fallback()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var path = Path.Combine(repo.Root, ".git", "config");
        var suffix = System.Text.Encoding.ASCII.GetBytes("\n[extra]\n opaque = ").Concat(new byte[] { 0xA4, 0xA4, 0x0A });
        await File.WriteAllBytesAsync(path, (await File.ReadAllBytesAsync(path)).Concat(suffix).ToArray());
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog(); using var native = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), native);
        var commands = new ConcurrentQueue<CommandEntry>(); runner.CommandExecuted += (_, entry) => commands.Enqueue(entry);
        var state = await service.OpenAsync(repo.Root, new(), default);
        Assert.NotEmpty(state.Commits); Assert.Single(state.Refs);
        Assert.Single(commands, entry => entry.Arguments.FirstOrDefault() == "config");
        Assert.Contains(commands, entry => entry.Arguments.FirstOrDefault() == "for-each-ref");
    }

    [Fact]
    public async Task Cold_open_resolves_admin_paths_once_and_reads_one_fresh_included_configuration_snapshot()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".git", "included.conf"),
            "[core]\n autocrlf = input\n hooksPath = hooks-original\n[remote \"included\"]\n url = https://example.invalid/first.git\n[i18n]\n commitEncoding = Big5\n");
        await repo.Git("config", "include.path", "included.conf");
        await repo.Git("config", "http.https://example.invalid/.extraHeader", "Authorization: Bearer gitgui-test-marker");
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        using var history = new LibHistoryReader();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction(), history);
        var commands = new ConcurrentQueue<CommandEntry>(); runner.CommandExecuted += (_, entry) => commands.Enqueue(entry);
        var first = await service.OpenAsync(repo.Root, new(), default);
        Assert.Equal("input", first.TextSettings.AutoCrlf); Assert.Equal("Big5", first.TextSettings.CommitEncoding);
        Assert.Contains(first.RiskySettings, value => value.Contains("hooks-original", StringComparison.Ordinal));
        Assert.Contains(first.Remotes, remote => remote.Name == "included" && remote.Url == "https://example.invalid/first.git");
        Assert.DoesNotContain(commands, entry => entry.Output.Contains("gitgui-test-marker", StringComparison.Ordinal));
        Assert.Single(commands, entry => entry.Arguments.FirstOrDefault() == "config");
        var descriptor = Assert.Single(commands, entry => entry.Arguments.FirstOrDefault() == "rev-parse");
        Assert.Contains("--show-toplevel", descriptor.Arguments); Assert.Contains("--git-common-dir", descriptor.Arguments);

        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".git", "included.conf"),
            "[core]\n autocrlf = false\n hooksPath = hooks-updated\n[remote \"included\"]\n url = https://example.invalid/second.git\n");
        commands.Clear();
        var second = await service.OpenAsync(repo.Root, new(), default);
        Assert.Equal("false", second.TextSettings.AutoCrlf); Assert.Equal("UTF-8", second.TextSettings.CommitEncoding);
        Assert.Contains(second.RiskySettings, value => value.Contains("hooks-updated", StringComparison.Ordinal));
        Assert.DoesNotContain(second.RiskySettings, value => value.Contains("hooks-original", StringComparison.Ordinal));
        Assert.Contains(second.Remotes, remote => remote.Url == "https://example.invalid/second.git");
        Assert.Single(commands, entry => entry.Arguments.FirstOrDefault() == "config");
    }
}
