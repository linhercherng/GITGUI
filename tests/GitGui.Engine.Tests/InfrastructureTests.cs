using GitGui.Core;
using GitGui.Infrastructure;
using Xunit;

namespace GitGui.Engine.Tests;

public class InfrastructureTests
{
    [Fact]
    public async Task Trust_and_snapshot_index_survive_database_reopening()
    {
        using var repo = new TemporaryRepository();
        var database = Path.Combine(repo.Root, "settings.db");
        var store = new WorkspaceStorage(database);
        Assert.False(await store.IsTrustedAsync(repo.Root, default));
        await store.TrustAsync(repo.Root, default);
        await store.SaveAsync(new("id", repo.Root, "before ' quoted", DateTimeOffset.UtcNow, "refs/gitgui/snapshots/id/state", null, 2), default);
        var reopened = new WorkspaceStorage(database);
        Assert.True(await reopened.IsTrustedAsync(repo.Root, default));
        Assert.Equal("before ' quoted", Assert.Single(await reopened.ListAsync(repo.Root, default)).Title);
        await reopened.RemoveAsync("id", default);
        Assert.Empty(await reopened.ListAsync(repo.Root, default));
    }

    [Fact]
    public async Task Askpass_uses_same_user_pipe_and_does_not_persist_answer()
    {
        var interaction = new Credentials();
        using var server = new AskPassBridge(interaction);
        var answer = await AskPassBridge.RequestAsync(server.PipeName, "Password for https://example.invalid", default);
        Assert.Equal("dummy-密碼-for-test", answer);
        Assert.Equal("Password for https://example.invalid", interaction.Prompt);
    }

    [Fact]
    public async Task A_malformed_client_does_not_disable_later_askpass_requests()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new AskPassBridge(new Credentials());
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await using (var client = new System.IO.Pipes.NamedPipeClientStream(".", server.PipeName,
                System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(timeout.Token);
                await client.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{invalid-json}\n"), timeout.Token);
                await client.FlushAsync(timeout.Token);
            }
            Assert.Equal("dummy-密碼-for-test", await AskPassBridge.RequestAsync(server.PipeName, "Password", timeout.Token));
        }
        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => AskPassBridge.RequestAsync(server.PipeName, "Password", timeout.Token)));
        Assert.All(simultaneous, answer => Assert.Equal("dummy-密碼-for-test", answer));
        server.Dispose();
    }

    private sealed class Credentials : IWorkspaceInteraction
    {
        public string? Prompt { get; private set; }
        public Task<ConfirmationAnswer> ConfirmAsync(Confirmation confirmation, CancellationToken ct) => Task.FromResult(new ConfirmationAnswer(false));
        public Task<string?> CredentialAsync(string prompt, CancellationToken ct) { Prompt = prompt; return Task.FromResult<string?>("dummy-密碼-for-test"); }
    }
}
