using System.Threading;

namespace DiscImageStudio.Services;

/// <summary>Holds the desktop instance for the lifetime of the main STA thread.</summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    // Stable across versions and installation paths; command-line jobs do not acquire it.
    private const string DesktopName = "DiscImageStudio.Desktop.8DC3340F-3DB1-47F4-B0F8-A42B3E7D4374";
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    internal static SingleInstanceGuard? TryAcquire(string name = DesktopName)
    {
        Mutex mutex = new(false, name, new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = true,
        });
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new SingleInstanceGuard(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mutex.ReleaseMutex(); }
        finally { _mutex.Dispose(); }
    }
}
