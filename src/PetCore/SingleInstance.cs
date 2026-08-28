using System.Threading;

namespace PetCore;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static bool TryAcquire(string name, out SingleInstance? instance)
    {
        var mutex = new Mutex(initiallyOwned: false, $"Global\\{name}");
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // 이전 소유자가 죽었다. 우리가 가져간다.
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            instance = null;
            return false;
        }

        instance = new SingleInstance(mutex);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
