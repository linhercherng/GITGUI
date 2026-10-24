using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GitGui.App;

internal static class WindowsNativeHost
{
    public static string? ActivePath()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(Environment.ProcessPath,
            Path.Combine(Environment.SystemDirectory, "rundll32.exe"), StringComparison.OrdinalIgnoreCase)) return null;
        var expected = Path.Combine(AppContext.BaseDirectory, "GitGui.Host.dll");
        var module = GetModuleHandleW(expected);
        var path = new StringBuilder(32768);
        if (module == 0 || GetModuleFileNameW(module, path, path.Capacity) == 0 ||
            !string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unrecognized native GITGUI host.");
        return expected;
    }

    // The parent script remains alive until the desktop exits. Use its actual OS creation
    // time, so changing the launcher cannot hide wrapper startup from the cold-start gate.
    public static LaunchOrigin Origin()
    {
        using var current = Process.GetCurrentProcess();
        var childStarted = current.StartTime.ToUniversalTime();
        var host = ActivePath();
        if (host is null) return new(childStarted, current.Id, current.Id, Environment.ProcessPath!, null);
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new InvalidOperationException("Cannot inspect native launcher parent.");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        uint parent = 0;
        if (Process32FirstW(snapshot, ref entry))
            do { if (entry.ProcessId == current.Id) { parent = entry.ParentProcessId; break; } } while (Process32NextW(snapshot, ref entry));
        using var launcher = Process.GetProcessById(checked((int)parent));
        var expected = Path.Combine(Environment.SystemDirectory, "wscript.exe");
        if (!string.Equals(launcher.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native bundle probes must include the normal WScript launcher.");
        var started = launcher.StartTime.ToUniversalTime();
        if (started > childStarted || childStarted - started > TimeSpan.FromMinutes(3))
            throw new InvalidOperationException("Invalid launcher creation time.");
        return new(started, current.Id, launcher.Id, expected, host);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? Executable;
    }
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(nint module, StringBuilder file, int size);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}

internal sealed record LaunchOrigin(DateTime StartedUtc, int ProcessId, int LauncherProcessId, string LauncherPath, string? NativeHostPath);
