namespace GitGui.App;

internal sealed record CompositionProbeSettings(bool Direct)
{
    public string Requested => Direct ? "direct" : "default";
    public static CompositionProbeSettings Parse(string? value, bool software, bool windows)
    {
        if (value is not (null or "" or "default" or "direct")) throw new ArgumentException("Probe composition must be default or direct.");
        var direct = value == "direct";
        if (direct && (!windows || software)) throw new ArgumentException("DirectComposition probes require Windows and cannot use the software-renderer experiment.");
        return new(direct);
    }
    public CompositionBackendEvidence Observe(string? graphics, string? factory) => new(Requested, graphics, factory,
        Direct ? graphics == "Avalonia.Win32.OpenGl.Angle.D3D11AngleWin32PlatformGraphics" &&
            factory == "Avalonia.Win32.DComposition.DirectCompositionConnection" : null);
}
internal sealed record CompositionBackendEvidence(string Requested, string? ActualGraphics, string? ActualSurfaceFactory, bool? RequestedBackendMatched);
