using GitGui.App;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class CompositionProbeSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    public void Default_preserves_platform_composition(string? value)
    {
        var settings = CompositionProbeSettings.Parse(value, software: false, windows: true);
        Assert.False(settings.Direct);
        Assert.Null(settings.Observe(null, null).RequestedBackendMatched);
    }
    [Fact]
    public void Direct_accepts_windows_hardware_mode_and_requires_exact_observed_backend()
    {
        var settings = CompositionProbeSettings.Parse("direct", software: false, windows: true);
        Assert.True(settings.Direct);
        var evidence = settings.Observe("Avalonia.Win32.OpenGl.Angle.D3D11AngleWin32PlatformGraphics", "Avalonia.Win32.DComposition.DirectCompositionConnection");
        Assert.True(evidence.RequestedBackendMatched); Assert.Equal("direct", evidence.Requested);
    }
    [Theory]
    [InlineData("unsupported", false, true)]
    [InlineData("DIRECT", false, true)]
    [InlineData("direct", true, true)]
    [InlineData("direct", false, false)]
    public void Invalid_experiments_are_rejected(string value, bool software, bool windows)
        => Assert.Throws<ArgumentException>(() => CompositionProbeSettings.Parse(value, software, windows));
    [Theory]
    [InlineData(null, null)]
    [InlineData("Avalonia.Win32.OpenGl.Angle.D3D11AngleWin32PlatformGraphics", null)]
    [InlineData("Avalonia.Win32.OpenGl.Angle.D3D11AngleWin32PlatformGraphics", "Avalonia.Win32.WinRT.Composition.WinUiCompositorConnection")]
    [InlineData("software", "Avalonia.Win32.DComposition.DirectCompositionConnection")]
    public void Missing_or_fallback_backend_cannot_pass_a_direct_request(string? graphics, string? factory)
        => Assert.False(CompositionProbeSettings.Parse("direct", false, true).Observe(graphics, factory).RequestedBackendMatched);
}
