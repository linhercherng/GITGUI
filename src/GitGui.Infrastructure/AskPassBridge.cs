using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GitGui.Core;

namespace GitGui.Infrastructure;

/// <summary>Secrets travel only over a same-user named pipe and Git's askpass stdout.</summary>
public sealed class AskPassBridge : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task loop;
    private int disposed;
    public string PipeName { get; } = "gitgui-askpass-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
    public AskPassBridge(IWorkspaceInteraction interaction) => loop = Serve(interaction);

    private async Task Serve(IWorkspaceInteraction interaction)
    {
        // Reuse the listening instance: disposing it between clients drops queued Unix connections.
        await using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(lifetime.Token);
                try
                {
                    using var input = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                    await using var output = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    var line = await input.ReadLineAsync(lifetime.Token);
                    if (line is null || line.Length > 8192) continue;
                    var prompt = JsonSerializer.Deserialize<string>(line) ?? "Git credential";
                    var answer = await interaction.CredentialAsync(prompt, lifetime.Token);
                    await output.WriteLineAsync(JsonSerializer.Serialize(answer).AsMemory(), lifetime.Token);
                }
                catch (IOException) { /* A cancelled Git client must not disable later prompts. */ }
                catch (JsonException) { /* Ignore malformed requests without retaining their contents. */ }
                finally { if (server.IsConnected) server.Disconnect(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (lifetime.IsCancellationRequested) { }
    }

    public static async Task<string?> RequestAsync(string pipe, string prompt, CancellationToken ct)
    {
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(15000, ct);
        using var input = new StreamReader(client, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        await using var output = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        await output.WriteLineAsync(JsonSerializer.Serialize(prompt).AsMemory(), ct);
        var line = await input.ReadLineAsync(ct);
        return line is null ? null : JsonSerializer.Deserialize<string?>(line);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        _ = loop.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
    }
}
