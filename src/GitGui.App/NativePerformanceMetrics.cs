using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;

namespace GitGui.App;

/// <summary>Opt-in native-probe diagnostics. A normal desktop session never constructs this listener.</summary>
internal sealed class NativePerformanceMetrics : IDisposable
{
    public const string MeterName = "Avalonia.Diagnostic.Meter";
    private readonly MeterListener listener = new();
    private readonly object gate = new();
    private readonly List<NativeMetricSample> samples = [];
    private readonly List<NativeMetricInstrument> instruments = [];
    private string phase = "startup";
    private int uiManagedThreadId;
    private uint? uiNativeThreadId;
    private bool disposed;
    public long TimestampFrequency => Stopwatch.Frequency;
    public int UiManagedThreadId => Volatile.Read(ref uiManagedThreadId);
    public uint? UiNativeThreadId => uiNativeThreadId;

    public NativePerformanceMetrics()
    {
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name != MeterName) return;
            lock (gate)
            {
                if (disposed) return;
                instruments.Add(new(instrument.Name, instrument.Unit, instrument.GetType().Name, instrument.Meter.Version));
            }
            current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Record(instrument, value));
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => Record(instrument, value));
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Record(instrument, value));
        listener.Start();
    }

    public void BindUiThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (UiManagedThreadId != 0 && UiManagedThreadId != current)
            throw new InvalidOperationException("The native probe cannot change its UI thread identity.");
        uiNativeThreadId = CurrentNativeThreadId();
        Volatile.Write(ref uiManagedThreadId, current);
    }
    public void SetPhase(string value) => Volatile.Write(ref phase, value);
    public void RecordObservableInstruments() => listener.RecordObservableInstruments();
    private void Record(Instrument instrument, double value)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var managed = Environment.CurrentManagedThreadId;
        var sample = new NativeMetricSample(instrument.Name, instrument.Unit, value, timestamp,
            managed, CurrentNativeThreadId(), managed == UiManagedThreadId, Volatile.Read(ref phase));
        lock (gate) { if (!disposed) samples.Add(sample); }
    }
    public IReadOnlyList<NativeMetricSample> Snapshot() { lock (gate) return samples.OrderBy(s => s.RecordedQpc).ToArray(); }
    public IReadOnlyList<NativeMetricInstrument> Instruments() { lock (gate) return instruments.ToArray(); }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        listener.Dispose();
    }
    private static uint? CurrentNativeThreadId() => OperatingSystem.IsWindows() ? GetCurrentThreadId() : null;
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
}

internal sealed record NativeMetricSample(string Instrument, string? Unit, double Value, long RecordedQpc,
    int ManagedThreadId, uint? NativeThreadId, bool IsUiThread, string Phase);
internal sealed record NativeMetricInstrument(string Name, string? Unit, string Kind, string? MeterVersion);
