using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GitGui.Core;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class TerminalPanelTests
{
    [AvaloniaFact]
    public async Task Panel_streams_bounded_session_transcript_sends_input_and_closes_on_detach()
    {
        var service = new FakeTerminal(); await using var panel = new TerminalPanel(service);
        var window = new Window { Content = panel }; window.Show();
        await panel.OpenAsync("/test/repository");
        Assert.True(panel.IsRunning); Assert.Equal("/test/repository", service.Root);
        var input = panel.Children.OfType<Grid>().Last().Children.OfType<TextBox>().Single();
        input.Text = "git status";
        var send = panel.Children.OfType<Grid>().Last().Children.OfType<Button>().Single(b => b.Name == "TerminalSend");
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("git status", Assert.Single(service.Session.Lines)); Assert.Empty(input.Text!);
        service.Session.Emit("output from child process\n");
        await Task.Delay(100); Dispatcher.UIThread.RunJobs();
        Assert.Contains("output from child process", panel.Children.OfType<TextBox>().Single().Text);
        window.Close(); await service.Session.Completion;
        Assert.False(panel.IsRunning); Assert.True(service.Session.Disposed);
    }

    [AvaloniaFact]
    public async Task Startup_failure_is_displayed_and_input_stays_disabled()
    {
        await using var panel = new TerminalPanel(new FailedTerminal());
        await panel.OpenAsync("/not/trusted");
        Assert.False(panel.IsRunning);
        Assert.Contains("Trust required", panel.Children.OfType<TextBlock>().First().Text);
        Assert.False(panel.Children.OfType<Grid>().Last().Children.OfType<TextBox>().Single().IsEnabled);
    }

    private sealed class FailedTerminal : ITerminalService
    {
        public Task<ITerminalSession> StartAsync(string repository, CancellationToken ct) => throw new InvalidOperationException("Trust required");
    }
    private sealed class FakeTerminal : ITerminalService
    {
        public string Root = "";
        public FakeSession Session { get; } = new();
        public Task<ITerminalSession> StartAsync(string repository, CancellationToken ct) { Root = repository; return Task.FromResult<ITerminalSession>(Session); }
    }
    private sealed class FakeSession : ITerminalSession
    {
        public string Repository => "/test/repository";
        public string GitExecutable => "/selected/bin/git";
        public int ProcessId => 123;
        public string Output { get; private set; } = "";
        public bool IsRunning => !Completion.IsCompleted;
        private readonly TaskCompletionSource<int> completion = new();
        public Task<int> Completion => completion.Task;
        public event EventHandler? OutputChanged;
        public List<string> Lines { get; } = [];
        public bool Disposed;
        public void Emit(string text) { Output += text; OutputChanged?.Invoke(this, EventArgs.Empty); }
        public Task SendLineAsync(string line, CancellationToken ct) { Lines.Add(line); return Task.CompletedTask; }
        public Task StopAsync() { completion.TrySetResult(0); return Task.CompletedTask; }
        public async ValueTask DisposeAsync() { Disposed = true; await StopAsync(); }
    }
}
