using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GitGui.Engine.Cli;

/// <summary>Owns terminal descendants even when a shell exits before its background children.</summary>
internal sealed class TerminalProcessLifetime : IDisposable
{
    private readonly Process process;
    private readonly bool unixProcessGroup;
    private SafeFileHandle? job;
    private int stopped;

    public TerminalProcessLifetime(Process process, bool unixProcessGroup)
    {
        this.process = process; this.unixProcessGroup = unixProcessGroup;
        if (!OperatingSystem.IsWindows()) return;
        var created = CreateJobObject(IntPtr.Zero, null);
        if (created.IsInvalid) { created.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            var limits = new ExtendedLimits { Basic = new() { Flags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(created, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(created, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            job = created;
        }
        catch { created.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        if (job is { } assigned) { assigned.Dispose(); job = null; }
        if (unixProcessGroup && !OperatingSystem.IsWindows()) _ = kill(-process.Id, 9);
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle handle, int informationClass, ref ExtendedLimits information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
}
