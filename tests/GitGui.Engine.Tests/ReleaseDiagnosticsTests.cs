using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Infrastructure;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class ReleaseDiagnosticsTests
{
    [Fact]
    public void Persistent_audit_masks_argument_secrets_and_never_records_stdout_or_arbitrary_hook_stderr()
    {
        using var folder = new TemporaryRepository();
        var logger = AppLogging.Create(folder.Root);
        AppLogging.Command(logger, new(DateTimeOffset.UtcNow, "formatted-command-secret", "stdout-secret", 1, TimeSpan.FromMilliseconds(12))
        {
            WorkingDirectory = folder.Root,
            Arguments = ["-c", "credential.helper=!echo helper-secret", "config", "--local", "http.extraheader", "Authorization: Bearer header-secret"],
            StandardError = "custom-hook-stderr-secret"
        });
        AppLogging.Command(logger, new(DateTimeOffset.UtcNow, "", "", 0, TimeSpan.FromMilliseconds(12))
        { Arguments = ["commit", "-m", "private-commit-message"] });
        ((IDisposable)logger).Dispose();
        var text = string.Join('\n', Directory.GetFiles(folder.Root, "gitgui-*.log").Select(File.ReadAllText));
        foreach (var secret in new[] { "formatted-command-secret", "stdout-secret", "helper-secret", "header-secret", "custom-hook-stderr-secret", "private-commit-message" }) Assert.DoesNotContain(secret, text);
        Assert.Contains("elapsedMs=12", text); Assert.Contains("exit=1", text); Assert.Contains("config", text);
    }

    [Fact]
    public async Task Cancelled_attempt_is_audited_without_stdout_or_credentials()
    {
        using var repo = new TemporaryRepository(); var runner = new GitProcessRunner(); CommandEntry? entry = null;
        runner.CommandExecuted += (_, value) => entry = value;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(repo.Root, ["status"], cancelled.Token));
        Assert.NotNull(entry); Assert.Equal(-1, entry.ExitCode); Assert.Equal("Cancelled", entry.StandardError);
        Assert.Equal(repo.Root, entry.WorkingDirectory);
    }

    [Fact]
    public async Task Command_audit_receives_separate_arguments_directory_and_stderr_without_parsing_stdout()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var runner = new GitProcessRunner(); CommandEntry? captured = null;
        runner.CommandExecuted += (_, entry) => captured = entry;
        var result = await runner.RunAsync(repo.Root, ["rev-parse", "--verify", "missing-audit-ref"], default);
        Assert.NotEqual(0, result.ExitCode); Assert.NotNull(captured);
        Assert.Equal(repo.Root, captured.WorkingDirectory);
        Assert.Equal(new[] { "rev-parse", "--verify", "missing-audit-ref" }, captured.Arguments);
        Assert.Equal(result.Error, captured.StandardError);
    }

    [Fact]
    public void Split_secret_options_and_proxy_credentials_are_redacted()
    {
        var text = GitProcessRunner.Redact("git --password split-secret --token token-secret\nProxy-Authorization: Basic proxy-secret\nssh://user:url-secret@example.invalid/repo");
        foreach (var secret in new[] { "split-secret", "token-secret", "proxy-secret", "url-secret" }) Assert.DoesNotContain(secret, text);
        Assert.Contains("example.invalid", text);
    }

    [Fact]
    public void Inactive_logs_older_than_fourteen_days_are_removed_even_when_fewer_than_fourteen_files_exist()
    {
        using var folder = new TemporaryRepository();
        var old = Path.Combine(folder.Root, "gitgui-20200101.log");
        var unrelated = Path.Combine(folder.Root, "notes.log");
        File.WriteAllText(old, "old diagnostic"); File.WriteAllText(unrelated, "user file");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        using (var logger = (IDisposable)AppLogging.Create(folder.Root)) { }
        Assert.False(File.Exists(old)); Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task Missing_remote_path_containing_hook_is_not_misclassified_as_a_hook_rejection()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var workspace = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var head = await repo.Git("rev-parse", "HEAD");
        await repo.Git("remote", "add", "origin", Path.Combine(repo.Root, "missing-hook-remote"));
        var result = await workspace.ExecuteAsync(repo.Root, new(GitAction.Fetch) { Remote = "origin" }, null, default);
        Assert.False(result.Success); Assert.Equal(GitErrorKind.Unknown, result.Error);
        Assert.Equal(head, await repo.Git("rev-parse", "HEAD"));
    }
}
