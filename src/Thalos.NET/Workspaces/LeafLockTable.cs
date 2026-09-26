namespace Thalos.Workspaces;

/// <summary>
/// One <see cref="SemaphoreSlim"/> per resolved leaf path, shared by every <see cref="WorkspaceTools"/> call in the
/// process, so <c>read_file</c>, <c>write_file</c> and <c>edit_file</c> on the same file take turns instead of
/// colliding on the leaf open's share mode. Keys compare case-insensitively, matching Windows' own path
/// comparison; on Linux that is a harmless over-approximation, since two differently-cased names only serialize.
/// </summary>
/// <remarks>
/// Entries are reference-counted and removed when the count reaches zero, so the table holds one entry per path
/// currently in use, never one per path ever used (round-4 ruling (l)). The count is taken and dropped under one
/// gate, together with the lookup and the removal: a caller that finds an entry has already counted itself before
/// the gate is released, so the entry cannot be removed and replaced while it waits on the semaphore, and two
/// callers for one key can never be handed different semaphores. Waiting on the semaphore itself happens outside
/// the gate. A lease from this table is the outermost lock in the order <see cref="DirectoryLevelTable"/> documents:
/// a holder may remove directory levels, but nothing waits for a leaf lease while holding a level's removal lock.
/// </remarks>
internal sealed class LeafLockTable
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>The process-wide table <see cref="WorkspaceTools"/> uses unless a test gives an instance its own.</summary>
    public static LeafLockTable Shared { get; } = new();

    /// <summary>How many paths currently have an entry: a caller holding the lock or waiting for it.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the lock on <paramref name="key"/>. Returns the held lease, which
    /// releases the lock and drops this caller's reference when disposed, or <see langword="null"/> when the wait ran
    /// out; the reference is dropped in that case too, and when the wait is cancelled.
    /// </summary>
    public async Task<Lease?> AcquireAsync(string key, TimeSpan timeout, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var found))
            {
                found = new Entry();
                _entries.Add(key, found);
            }

            found.References++;
            entry = found;
        }

        var entered = false;
        try
        {
            entered = await entry.Semaphore.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!entered)
            {
                Leave(key, entry);
            }
        }

        return entered ? new Lease(this, key, entry) : null;
    }

    private void Leave(string key, Entry entry)
    {
        lock (_gate)
        {
            entry.References--;
            if (entry.References == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    internal sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Callers holding or waiting for this entry. Read and written only under the table's gate.</summary>
        public int References { get; set; }
    }

    /// <summary>A held lock on one leaf path. Disposing it releases the lock and drops the holder's reference, once.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly LeafLockTable _table;
        private readonly string _key;
        private Entry? _entry;

        internal Lease(LeafLockTable table, string key, Entry entry)
        {
            _table = table;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _entry, null) is not { } entry)
            {
                return;
            }

            entry.Semaphore.Release();
            _table.Leave(_key, entry);
        }
    }
}
