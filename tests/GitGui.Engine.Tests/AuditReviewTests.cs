using System.Reflection;
using GitGui.Core;
using GitGui.Engine.Cli;
using GitGui.Infrastructure;
using Serilog;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class AuditReviewTests
{
    [Theory]
    [InlineData("--message=private-message")]
    [InlineData("-mprivate-message")]
    public void Attached_commit_message_forms_never_reach_persistent_audit_arguments(string argument)
    {
        var sanitized = string.Join(" ", GitLogSanitizer.Arguments(["commit", argument]));
        Assert.DoesNotContain("private-message", sanitized); Assert.Contains("commit", sanitized);
    }

    [Theory]
    [InlineData("--access_token")]
    [InlineData("--accesstoken")]
    [InlineData("--PASSWORD")]
    [InlineData("--TOKEN")]
    public void Split_secret_aliases_use_the_same_case_insensitive_policy_as_text_redaction(string option)
    {
        var sanitized = string.Join(" ", GitLogSanitizer.Arguments(["helper", option, "private-credential"]));
        Assert.DoesNotContain("private-credential", sanitized);
    }

    [Fact]
    public void Throwing_logger_does_not_escape_the_persistent_audit_callback()
    {
        var logger = DispatchProxy.Create<ILogger, ThrowingLoggerProxy>();
        var error = Record.Exception(() => AppLogging.Command(logger,
            new(DateTimeOffset.UtcNow, "", "", 0, TimeSpan.Zero) { Arguments = ["status"] }));
        Assert.Null(error);
    }

    [Fact]
    public async Task Throwing_audit_observer_does_not_change_completed_Git_write_or_skip_other_observers()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var head = (await repo.Git("rev-parse", "HEAD")).Trim(); var runner = new GitProcessRunner();
        CommandEntry? captured = null;
        runner.CommandExecuted += (_, _) => throw new IOException("diagnostic observer unavailable");
        runner.CommandExecuted += (_, entry) => captured = entry;
        var result = await runner.RunAsync(repo.Root, ["update-ref", "refs/heads/audit-observer", head], default,
            new() { IsReadOnly = false, Trusted = true });
        Assert.Equal(0, result.ExitCode); Assert.NotNull(captured); Assert.Equal(0, captured.ExitCode);
        Assert.Equal(head, (await repo.Git("rev-parse", "refs/heads/audit-observer")).Trim());
    }

    [Theory]
    [InlineData("private-split-token", "private-split-token")]
    [InlineData("head'private-tail", "private-tail")]
    [InlineData("head private-spaced-tail", "private-spaced-tail")]
    public async Task Displayed_command_masks_split_secret_options_and_URL_scoped_config_credentials(string secret, string forbiddenFragment)
    {
        using var repo = new TemporaryRepository(); var runner = new GitProcessRunner(); CommandEntry? captured = null;
        runner.CommandExecuted += (_, entry) => captured = entry;
        await runner.RunAsync(repo.Root, ["--token", secret, "--version"], default);
        Assert.NotNull(captured); Assert.DoesNotContain(forbiddenFragment, captured.Command);
        await runner.RunAsync(repo.Root, ["-c", "http.https://user:private-url-token@example.invalid/.extraheader=public", "--version"], default);
        Assert.NotNull(captured); Assert.DoesNotContain("private-url-token", captured.Command);
        var arguments = GitLogSanitizer.Arguments(["-c", "http.https://user:private-url-token@example.invalid/.extraheader=public", "--version"]);
        Assert.DoesNotContain("private-url-token", string.Join(' ', arguments));
    }

    [Fact]
    public void Explicit_authentication_failure_takes_priority_over_unrelated_unmerged_entries()
        => Assert.Equal(GitErrorKind.AuthFailed, GitFailurePolicy.Classify(1, "Authentication failed", hasUnmergedEntries: true));

    [Fact]
    public void Display_arguments_preserve_copyable_normal_config_messages_and_long_argument_lists()
    {
        var original = new[] { "-c", "core.longpaths=true", "commit", "-m", "normal message" }
            .Concat(Enumerable.Range(0, 70).Select(i => "path-" + i)).ToArray();
        Assert.Equal(original, GitLogSanitizer.DisplayArguments(original));
        var secrets = GitLogSanitizer.DisplayArguments(["-c", "credential.helper=!echo private-helper", "--header=private-header with spaces",
            "--token=private-token with spaces", "--token", "head'private-tail"]);
        foreach (var secret in new[] { "private-helper", "private-header", "private-token", "private-tail" }) Assert.DoesNotContain(secret, string.Join(' ', secrets));
        Assert.DoesNotContain("private=part", string.Join(' ', GitLogSanitizer.Arguments(["-c", "http.https://user:private=part@example.invalid/.extraheader=header"])));
    }


    public class ThrowingLoggerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new IOException("diagnostic logger unavailable");
    }
}
