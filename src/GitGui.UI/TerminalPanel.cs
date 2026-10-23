using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitGui.Core;

namespace GitGui.UI;

/// <summary>Embedded persistent line terminal. The panel never logs or persists commands.</summary>
public sealed class TerminalPanel : Grid, IAsyncDisposable
{
    private readonly ITerminalService service;
    private readonly Localization localization;
    private string? statusKey = "TerminalClosed";
    private object?[] statusArguments = [];
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox transcript = new() { Name = "TerminalOutput", IsReadOnly = true, AcceptsReturn = true, FontSize = 12,
        FontFamily = new("Cascadia Code,Consolas,Menlo,Microsoft JhengHei,PingFang TC,Noto Sans TC,monospace") };
    private readonly TextBox input = new() { Name = "TerminalInput", IsEnabled = false };
    private readonly Button send = new ActionButton { Icon = "Command", Name = "TerminalSend", IsEnabled = false };
    private readonly Button close = new ActionButton { Icon = "Close", Name = "TerminalClose" };
    private readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private ITerminalSession? session;
    private CancellationTokenSource? sessionLifetime;
    private long version;
    private int transcriptChanged;
    public bool IsRunning => session?.IsRunning == true;
    public event EventHandler? RunningChanged;

    public TerminalPanel(ITerminalService service, Localization? localization = null)
    {
        this.service = service; this.localization = localization ?? new();
        this.localization.Bind(input, TextBox.WatermarkProperty, "TerminalInput"); this.localization.Bind(send, ContentControl.ContentProperty, "TerminalSend"); this.localization.Bind(close, ContentControl.ContentProperty, "TerminalClose");
        this.localization.Bind(input, Avalonia.Automation.AutomationProperties.NameProperty, "TerminalInput"); this.localization.Bind(transcript, Avalonia.Automation.AutomationProperties.NameProperty, "TerminalOutput");
        SetStatus("TerminalClosed");
        ScrollViewer.SetHorizontalScrollBarVisibility(transcript, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(transcript, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        RowDefinitions = new("Auto,Auto,*,Auto"); RowSpacing = 8; MinHeight = 250;
        Children.Add(status);
        var help = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        };
        this.localization.Bind(help, TextBlock.TextProperty, "TerminalHelp");
        AppearanceManager.Bind(help, TextBlock.ForegroundProperty, "MutedBrush");
        SetRow(help, 1); Children.Add(help); SetRow(transcript, 2); Children.Add(transcript);
        var commandRow = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
        commandRow.Children.Add(input); SetColumn(send, 1); commandRow.Children.Add(send); SetColumn(close, 2); commandRow.Children.Add(close);
        SetRow(commandRow, 3); Children.Add(commandRow);
        input.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SendAsync(); } };
        send.Click += async (_, _) => await SendAsync();
        close.Click += async (_, _) => await CloseAsync();
        renderTimer.Tick += (_, _) => RenderTranscript();
        renderTimer.Start();
    }

    public async Task OpenAsync(string repository, CancellationToken ct = default)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { await Dispatcher.UIThread.InvokeAsync(() => OpenAsync(repository, ct)); return; }
        await CloseAsync();
        var opening = ++version;
        var lifetime = sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        SetStatus("TerminalWaiting");
        try
        {
            var started = await service.StartAsync(repository, lifetime.Token);
            if (opening != version || lifetime.IsCancellationRequested) { await started.DisposeAsync(); return; }
            session = started; started.OutputChanged += OutputChanged;
            statusKey = null; status.Text = $"{started.Repository} · {started.GitExecutable}";
            input.IsEnabled = send.IsEnabled = started.IsRunning;
            Interlocked.Exchange(ref transcriptChanged, 1); RenderTranscript();
            RunningChanged?.Invoke(this, EventArgs.Empty);
            input.Focus();
            _ = ObserveExitAsync(started, opening);
        }
        catch (OperationCanceledException) { if (opening == version) SetStatus("TerminalCancelled"); }
        catch (Exception ex) { if (opening == version) SetError(ex.Message); }
    }

    private async Task ObserveExitAsync(ITerminalSession observed, long opening)
    {
        try { var exit = await observed.Completion; if (opening == version) SetStatus("TerminalExited", exit); }
        catch (Exception ex) { if (opening == version) SetError(ex.Message); }
        finally
        {
            if (opening == version)
            {
                input.IsEnabled = send.IsEnabled = false;
                Interlocked.Exchange(ref transcriptChanged, 1); RenderTranscript(); RunningChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
    private void OutputChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref transcriptChanged, 1);
    private void RenderTranscript()
    {
        if (Interlocked.Exchange(ref transcriptChanged, 0) == 0 || session is null) return;
        transcript.Text = session.Output; transcript.CaretIndex = transcript.Text?.Length ?? 0;
    }
    private async Task SendAsync()
    {
        var active = session; var command = input.Text ?? "";
        if (active?.IsRunning != true) return;
        input.Text = "";
        try { await active.SendLineAsync(command, sessionLifetime?.Token ?? default); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or ArgumentException) { SetError(ex.Message); }
    }
    public async Task CloseAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { await Dispatcher.UIThread.InvokeAsync(CloseAsync); return; }
        ++version;
        var endingLifetime = sessionLifetime; sessionLifetime = null;
        endingLifetime?.Cancel();
        var ending = session; session = null;
        input.IsEnabled = send.IsEnabled = false;
        if (ending is not null)
        {
            ending.OutputChanged -= OutputChanged;
            try { await ending.DisposeAsync(); }
            catch (Exception ex) { SetError(ex.Message); }
            transcript.Text = ending.Output;
        }
        endingLifetime?.Dispose();
        SetStatus("TerminalClosed");
        RunningChanged?.Invoke(this, EventArgs.Empty);
    }
    private void SetError(string message) { statusKey = null; status.Text = message; }
    private void SetStatus(string key, params object?[] arguments) { statusKey = key; statusArguments = arguments; status.Text = localization.Format(key, arguments); }
    private void LocaleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (statusKey is { } key) status.Text = localization.Format(key, statusArguments); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); localization.PropertyChanged += LocaleChanged; renderTimer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        localization.PropertyChanged -= LocaleChanged; renderTimer.Stop(); _ = CloseAsync(); base.OnDetachedFromVisualTree(e);
    }
    public async ValueTask DisposeAsync() { renderTimer.Stop(); await CloseAsync(); }
}
