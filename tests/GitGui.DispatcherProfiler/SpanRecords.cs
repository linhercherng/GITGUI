internal sealed class SpanStrings
{
    private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
    private readonly List<string?> values = [null];
    public int Intern(string? value)
    {
        if (value is null) return 0;
        lock (ids)
        {
            if (ids.TryGetValue(value, out var id)) return id;
            id = values.Count; values.Add(value); ids.Add(value, id); return id;
        }
    }
    public string? Resolve(int id) { lock (ids) return values[id]; }
}

// Keep GC references out of the million-entry hot buffer; restore all strings after measurement.
internal struct RawSpanRecord
{
    public int Id, Parent, Seam, Callback, Phase, ExitPhase, ExceptionType;
    public long EnterQpc, ExitQpc;
    public uint NativeTid;
    public bool Completed;
    public readonly SpanRecord Project(SpanStrings strings) => new()
    {
        Id = Id, Parent = Parent, Seam = strings.Resolve(Seam), Callback = strings.Resolve(Callback),
        Phase = strings.Resolve(Phase), ExitPhase = strings.Resolve(ExitPhase), ExceptionType = strings.Resolve(ExceptionType),
        EnterQpc = EnterQpc, ExitQpc = ExitQpc, NativeTid = NativeTid, Completed = Completed,
        ProductPhaseAtEnter = null, ProductPhaseAtExit = null
    };
}

internal struct SpanRecord
{
    public int Id, Parent;
    public string? Seam, Callback, Phase, ExitPhase, ExceptionType, ProductPhaseAtEnter, ProductPhaseAtExit;
    public long EnterQpc, ExitQpc;
    public uint NativeTid;
    public bool Completed;
}
