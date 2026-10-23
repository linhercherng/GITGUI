using System.Text;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task Feeds_stdin_without_shell_and_reports_actual_command()
    {
        var runner = new GitProcessRunner();
        var records = new List<GitGui.Core.CommandEntry>();
        runner.CommandExecuted += (_, entry) => records.Add(entry);
        var result = await runner.RunAsync(Path.GetTempPath(), ["hash-object", "--stdin"], default,
            new GitRunOptions { Input = Encoding.UTF8.GetBytes("hello\n") });
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", Encoding.UTF8.GetString(result.Output).Trim());
        Assert.Contains("hash-object", Assert.Single(records).Command);
    }

    [Theory]
    [InlineData("https://user:password@example.invalid/repo", "password")]
    [InlineData("Authorization: Bearer abcdef123456", "abcdef123456")]
    public void Secrets_are_not_retained_in_logs(string value, string secret)
        => Assert.DoesNotContain(secret, GitProcessRunner.Redact(value));

    [Fact]
    public async Task Synchronous_stream_consumer_can_wait_for_a_command_that_requires_stdin()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var consumed = "";
        var result = await new GitProcessRunner().RunAsync(Path.GetTempPath(), ["hash-object", "--stdin"], timeout.Token,
            new() { Input = Encoding.UTF8.GetBytes("hello\n"), OutputConsumer = (stream, _) =>
            {
                using var reader = new StreamReader(stream);
                consumed = reader.ReadToEnd();
                return Task.CompletedTask;
            } });
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", consumed.Trim());
    }

    [Fact]
    public async Task A_cancelled_read_never_starts_a_child_process()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GitProcessRunner().ReadAsync(Path.GetTempPath(), ["--version"], cancellation.Token));
    }
}
