using System.Diagnostics;
using GitGui.App;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class WindowsNativeHostTests
{
    [Fact]
    public void Ordinary_process_uses_its_actual_os_creation_time()
    {
        using var process = Process.GetCurrentProcess();
        var origin = WindowsNativeHost.Origin();
        Assert.Equal(process.Id, origin.ProcessId);
        Assert.Equal(process.Id, origin.LauncherProcessId);
        Assert.Equal(process.StartTime.ToUniversalTime(), origin.StartedUtc);
        Assert.Equal(Environment.ProcessPath, origin.LauncherPath);
        Assert.Null(origin.NativeHostPath);
    }

    [Fact]
    public void Environment_variable_cannot_claim_an_active_native_host()
    {
        // The implementation inspects the actual process and loaded module instead of this value.
        var previous = Environment.GetEnvironmentVariable("GITGUI_NATIVE_HOST_PATH");
        try
        {
            Environment.SetEnvironmentVariable("GITGUI_NATIVE_HOST_PATH", "untrusted-host.dll");
            Assert.Null(WindowsNativeHost.ActivePath());
        }
        finally { Environment.SetEnvironmentVariable("GITGUI_NATIVE_HOST_PATH", previous); }
    }
}
