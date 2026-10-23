using System.Diagnostics;
using System.Text.RegularExpressions;
using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public sealed class TerminalTests
{
    [Fact]
    public async Task Persistent_shell_retains_variables_starts_in_repository_and_uses_selected_Git()
    {
        using var repo = new TemporaryRepository();
        var runtime = new GitRuntime(baseDirectory: repo.Root);
        var git = GitRuntimeTests.FindSystemGit(); await runtime.SaveAsync(git, default);
        if (OperatingSystem.IsWindows()) await File.WriteAllTextAsync(Path.Combine(repo.Root, "git.cmd"), "@echo REPOSITORY_GIT_SHADOW\r\n");
        var settings = new TrustSettings { Trusted = true };
        var lease = new Lease();
        await using var session = await new TerminalService(runtime, settings, acquireLease: (_, _) => Task.FromResult<IAsyncDisposable>(lease)).StartAsync(repo.Root, default);
        Assert.Equal(repo.Root, session.Repository); Assert.Equal(git, session.GitExecutable);
        await session.SendLineAsync(OperatingSystem.IsWindows() ? "set GITGUI_TEST_VALUE=persisted" : "GITGUI_TEST_VALUE=persisted", default);
        await session.SendLineAsync(OperatingSystem.IsWindows() ? "echo VALUE:%GITGUI_TEST_VALUE%" : "echo VALUE:$GITGUI_TEST_VALUE", default);
        await session.SendLineAsync(OperatingSystem.IsWindows() ? "cd" : "pwd", default);
        await session.SendLineAsync("git --version", default);
        await session.SendLineAsync(OperatingSystem.IsWindows() ? "where git" : "command -v git", default);
        await WaitFor(() => session.Output.Contains("VALUE:persisted") && session.Output.Contains("git version "));
        await WaitFor(() => session.Output.Contains(git));
        Assert.Contains(repo.Root, session.Output); Assert.DoesNotContain("REPOSITORY_GIT_SHADOW", session.Output); Assert.Equal(0, lease.Disposed);
        await session.SendLineAsync("exit", default);
        Assert.Equal(0, await session.Completion.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(1, lease.Disposed); Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task Untrusted_repository_cannot_start_shell_and_trust_gate_must_actually_grant_trust()
    {
        using var repo = new TemporaryRepository(); var settings = new TrustSettings();
        var runtime = new GitRuntime(baseDirectory: repo.Root); var called = false;
        var blocked = new TerminalService(runtime, settings, (_, _) => { called = true; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.StartAsync(repo.Root, default));
        Assert.True(called);
        var allowed = new TerminalService(runtime, settings, (_, _) => { settings.Trusted = true; return Task.CompletedTask; });
        await using var session = await allowed.StartAsync(repo.Root, default);
        Assert.True(session.IsRunning);
    }

    [Fact]
    public async Task Cancellation_kills_shell_and_descendants_and_releases_repository_lease_once()
    {
        using var repo = new TemporaryRepository(); using var cancellation = new CancellationTokenSource();
        var lease = new Lease();
        var service = new TerminalService(new GitRuntime(baseDirectory: repo.Root), new TrustSettings { Trusted = true }, acquireLease: (_, _) => Task.FromResult<IAsyncDisposable>(lease));
        await using var session = await service.StartAsync(repo.Root, cancellation.Token);
        await session.SendLineAsync(OperatingSystem.IsWindows()
            ? "powershell.exe -NoLogo -NoProfile -Command \"[Console]::WriteLine('CHILD_PID:' + [System.Diagnostics.Process]::GetCurrentProcess().Id); Start-Sleep -Seconds 60\""
            : "sh -c 'echo CHILD_PID:$$; sleep 60'", default);
        int childPid = 0;
        try { await WaitFor(() => { var match = Regex.Match(session.Output, @"CHILD_PID:(\d{2,})"); if (match.Success) childPid = int.Parse(match.Groups[1].Value); return childPid > 0; }); }
        catch (OperationCanceledException) { Assert.Fail(session.Output); }
        cancellation.Cancel(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(IsAlive(session.ProcessId)); await WaitFor(() => !IsAlive(childPid));
        Assert.Equal(1, lease.Disposed);
        await session.StopAsync(); Assert.Equal(1, lease.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendLineAsync("echo too-late", default));
    }

    [Fact]
    public async Task Transcript_is_bounded_and_redacts_credentials_in_commands_and_output()
    {
        using var repo = new TemporaryRepository();
        await using var session = await new TerminalService(new GitRuntime(baseDirectory: repo.Root), new TrustSettings { Trusted = true }).StartAsync(repo.Root, default);
        await session.SendLineAsync("echo https://alice:private@host.invalid token=hidden", default);
        await session.SendLineAsync(OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,5000) do @echo abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789"
            : "i=0; while [ \"$i\" -lt 5000 ]; do echo abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789; i=$((i+1)); done", default);
        await session.SendLineAsync("echo END_OF_TERMINAL_TEST", default);
        await WaitFor(() => session.Output.Contains("\nEND_OF_TERMINAL_TEST"));
        Assert.True(session.Output.Length <= TerminalService.MaxTranscriptCharacters);
        Assert.DoesNotContain("alice:private", session.Output); Assert.DoesNotContain("token=hidden", session.Output);
    }

    [Fact]
    public async Task Failed_runtime_selection_releases_lease_without_launching_another_git()
    {
        using var repo = new TemporaryRepository(); var lease = new Lease();
        var service = new TerminalService(new GitRuntime(baseDirectory: repo.Root, searchPath: ""), new TrustSettings { Trusted = true }, acquireLease: (_, _) => Task.FromResult<IAsyncDisposable>(lease));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(repo.Root, default));
        Assert.Equal(1, lease.Disposed);
    }

    [Fact]
    public async Task Windows_job_closes_background_child_even_when_shell_exits_naturally()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows Job ownership is a platform-specific guarantee.
        using var repo = new TemporaryRepository();
        await using var session = await new TerminalService(new GitRuntime(baseDirectory: repo.Root), new TrustSettings { Trusted = true }).StartAsync(repo.Root, default);
        await session.SendLineAsync("start /B powershell.exe -NoLogo -NoProfile -Command \"[Console]::WriteLine('BACKGROUND_PID:' + [System.Diagnostics.Process]::GetCurrentProcess().Id); Start-Sleep -Seconds 60\"", default);
        int childPid = 0;
        await WaitFor(() => { var match = Regex.Match(session.Output, @"BACKGROUND_PID:(\d{2,})"); if (match.Success) childPid = int.Parse(match.Groups[1].Value); return childPid > 0; });
        await session.SendLineAsync("exit", default);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        await WaitFor(() => !IsAlive(childPid));
    }

    private static bool IsAlive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private sealed class TrustSettings : IRepositorySettings
    {
        public bool Trusted;
        public Task<bool> IsTrustedAsync(string root, CancellationToken ct) => Task.FromResult(Trusted);
        public Task TrustAsync(string root, CancellationToken ct) { Trusted = true; return Task.CompletedTask; }
    }
    private sealed class Lease : IAsyncDisposable
    {
        public int Disposed;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposed); return ValueTask.CompletedTask; }
    }
}
