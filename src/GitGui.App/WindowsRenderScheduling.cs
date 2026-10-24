using System.Reflection;
using Avalonia;

namespace GitGui.App;

internal static class WindowsRenderScheduling
{
    // Keep Avalonia's official RenderLoop; replace only its timer before Win32 initializes it.
    // The private constructor is pinned to the version in Directory.Packages.props.
    public static IDisposable Install()
    {
        var assembly = typeof(AvaloniaObject).Assembly;
        if (assembly.GetName().Version != new Version(11, 3, 22, 0))
            throw new NotSupportedException("Review Windows render scheduling before upgrading Avalonia.");
        var locatorType = assembly.GetType("Avalonia.AvaloniaLocator", true)!;
        var locator = locatorType.GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var loopInterface = assembly.GetType("Avalonia.Rendering.IRenderLoop", true)!;
        if (locatorType.GetMethod("GetService")!.Invoke(locator, [loopInterface]) is not null)
            throw new InvalidOperationException("Windows render scheduling must be installed before platform initialization.");
        var timer = DispatchProxy.Create(assembly.GetType("Avalonia.Rendering.IRenderTimer", true)!, typeof(WindowsRenderTimerProxy));
        try
        {
            var loopType = assembly.GetType("Avalonia.Rendering.RenderLoop", true)!;
            var loop = Activator.CreateInstance(loopType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [timer], null)!;
            var registration = locatorType.GetMethod("Bind")!.MakeGenericMethod(loopInterface).Invoke(locator, null)!;
            registration.GetType().GetMethod("ToConstant")!.MakeGenericMethod(loopType).Invoke(registration, [loop]);
            return (IDisposable)timer;
        }
        catch { ((IDisposable)timer).Dispose(); throw; }
    }
}
