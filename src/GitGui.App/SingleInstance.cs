using System.Security.Cryptography;
using System.Text;

namespace GitGui.App;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private SingleInstance(Mutex mutex) => this.mutex = mutex;
    internal static string NameFor(string directory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return "GITGUI-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    }
    internal static SingleInstance? TryAcquire(string directory)
    {
        var mutex = new Mutex(false, NameFor(directory), new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new(mutex);
            mutex.Dispose();
            return null;
        }
        catch { mutex.Dispose(); throw; }
    }
    public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
}
