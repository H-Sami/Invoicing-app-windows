namespace MHC.Invoicing.Application.Runtime;

public sealed class SingleInstanceLease : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceLease(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static bool TryAcquire(string name, out SingleInstanceLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Mutex mutex = new(initiallyOwned: false, name);
        bool acquired;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                lease = null;
                return false;
            }

            lease = new SingleInstanceLease(mutex);
            return true;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _disposed = true;
    }
}
