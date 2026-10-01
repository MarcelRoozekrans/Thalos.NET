namespace Thalos.Sandbox.Docker;

/// <summary>One <see cref="SemaphoreSlim"/> per key, created on first use and dropped when nobody holds or waits for it.</summary>
internal sealed class KeyedLock
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>Waits up to <paramref name="timeout"/> for the key; returns null when the wait timed out.</summary>
    public async ValueTask<IDisposable?> TryAcquireAsync(string key, TimeSpan timeout, CancellationToken ct)
    {
        Entry entry;
        lock (entries)
        {
            if (!entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                entries[key] = entry;
            }

            entry.Users++;
        }

        var acquired = false;
        try
        {
            acquired = await entry.Gate.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!acquired)
            {
                Leave(key, entry);
            }
        }

        return acquired ? new Releaser(this, key, entry) : null;
    }

    /// <summary>How many keys currently have an entry; for tests.</summary>
    internal int Count
    {
        get
        {
            lock (entries)
            {
                return entries.Count;
            }
        }
    }

    private void Leave(string key, Entry entry)
    {
        lock (entries)
        {
            if (--entry.Users == 0)
            {
                entries.Remove(key);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Releaser(KeyedLock owner, string key, Entry entry) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                entry.Gate.Release();
                owner.Leave(key, entry);
            }
        }
    }
}
