namespace TraktMyYear.Infrastructure.Authentication;

public sealed class AccountTokenRefreshCoordinator
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable> AcquireAsync(string accountId, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (gate)
        {
            if (!entries.TryGetValue(accountId, out entry!))
            {
                entry = new Entry();
                entries.Add(accountId, entry);
            }

            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            return new Lease(this, accountId, entry);
        }
        catch
        {
            ReleaseReference(accountId, entry, releaseSemaphore: false);
            throw;
        }
    }

    private void ReleaseReference(string accountId, Entry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            entry.Semaphore.Release();
        }

        lock (gate)
        {
            entry.References--;
            if (entry.References == 0 && entries.TryGetValue(accountId, out var current) && ReferenceEquals(current, entry))
            {
                entries.Remove(accountId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class Lease(AccountTokenRefreshCoordinator owner, string accountId, Entry entry) : IAsyncDisposable
    {
        private int disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.ReleaseReference(accountId, entry, releaseSemaphore: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
