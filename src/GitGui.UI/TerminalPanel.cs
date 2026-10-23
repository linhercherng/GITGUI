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
    private readonly TextBlock status = new() { Text = "終端機未啟動 / Terminal closed", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox transcript = new() { Name = "TerminalOutput", IsReadOnly = true, AcceptsReturn = true, FontSize = 12,
        FontFamily = new("Cascadia Code,Consolas,Menlo,Microsoft JhengHei,PingFang TC,Noto Sans TC,monospace") };
    private readonly TextBox input = new() { Name = "TerminalInput", Watermark = "輸入指令，Enter 執行 / Enter command", IsEnabled = false };
    private readonly Button send = new ActionButton { Icon = "Command", Name = "TerminalSend", Content = "執行 / Send", IsEnabled = false };
    private readonly Button close = new ActionButton { Icon = "Close", Name = "TerminalClose", Content = "關閉終端機 / Close terminal" };
    private readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private ITerminalSession? session;
    private CancellationTokenSource? sessionLifetime;
    private long version;
    private int transcriptChanged;
    public bool IsRunning => session?.IsRunning == true;
    public event EventHandler? RunningChanged;

    public TerminalPanel(ITerminalService service)
    {
        this.service = service;
        ScrollViewer.SetHorizontalScrollBarVisibility(transcript, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(transcript, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        RowDefinitions = new("Auto,Auto,*,Auto"); RowSpacing = 8; MinHeight = 250;
        Children.Add(status);
        var help = new TextBlock
        {
            Text = "持續執行的文字終端機。啟動於目前倉庫，使用所選 Git。關閉後才可執行 GUI 寫入。\nPersistent line shell; close it before GUI writes. VT/full-screen editors are unsupported; use external tools for vim/top.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        };
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
        status.Text = "等待倉庫佇列並啟動終端機… / Waiting for repository queue…";
        try
        {
            var started = await service.StartAsync(repository, lifetime.Token);
            if (opening != version || lifetime.IsCancellationRequested) { await started.DisposeAsync(); return; }
            session = started; started.OutputChanged += OutputChanged;
            status.Text = $"{started.Repository} · {started.GitExecutable}";
            input.IsEnabled = send.IsEnabled = started.IsRunning;
            Interlocked.Exchange(ref transcriptChanged, 1); RenderTranscript();
            RunningChanged?.Invoke(this, EventArgs.Empty);
            input.Focus();
            _ = ObserveExitAsync(started, opening);
        }
        catch (OperationCanceledException) { if (opening == version) status.Text = "終端機已取消 / Terminal cancelled"; }
        catch (Exception ex) { if (opening == version) status.Text = ex.Message; }
    }

    private async Task ObserveExitAsync(ITerminalSession observed, long opening)
    {
        try { var exit = await observed.Completion; if (opening == version) status.Text = $"終端機已結束 / Terminal exited ({exit})"; }
        catch (Exception ex) { if (opening == version) status.Text = ex.Message; }
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
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or ArgumentException) { status.Text = ex.Message; }
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
            catch (Exception ex) { status.Text = ex.Message; }
            transcript.Text = ending.Output;
        }
        endingLifetime?.Dispose();
        status.Text = "終端機已關閉 / Terminal closed";
        RunningChanged?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); renderTimer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        renderTimer.Stop(); _ = CloseAsync(); base.OnDetachedFromVisualTree(e);
    }
    public async ValueTask DisposeAsync() { renderTimer.Stop(); await CloseAsync(); }
}
