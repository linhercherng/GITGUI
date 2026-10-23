namespace GitGui.Core;

/// <summary>A persistent, redirected line shell; VT screens and full-screen editors are not supported.</summary>
public interface ITerminalService
{
    Task<ITerminalSession> StartAsync(string repository, CancellationToken ct);
}

public interface ITerminalSession : IAsyncDisposable
{
    string Repository { get; }
    string GitExecutable { get; }
    int ProcessId { get; }
    string Output { get; }
    bool IsRunning { get; }
    Task<int> Completion { get; }
    /// <summary>Only announces a changed bounded transcript. Read Output to obtain its latest value.</summary>
    event EventHandler? OutputChanged;
    Task SendLineAsync(string line, CancellationToken ct);
    Task StopAsync();
}
