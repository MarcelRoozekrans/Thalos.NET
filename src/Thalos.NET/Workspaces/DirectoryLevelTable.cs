namespace Thalos.Workspaces;

/// <summary>
/// Counts, per directory level, how many pinned chains in this process currently hold it, so a call that created a
/// level and is now cleaning it up never removes one another call is still using (round-5 ruling (t)). On Linux,
/// <c>unlinkat</c> with <c>AT_REMOVEDIR</c> removes an empty directory whatever descriptors are open on it, and on
/// Windows the removal's own <c>DELETE</c> handle makes a concurrent pin of the same level fail with a sharing
/// violation. Either way a legitimate concurrent writer was refused. Keys are a level's canonical path: compared
/// case-insensitively on Windows, matching its file system, and ordinally elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gate discipline, as in <see cref="LeafLockTable"/>.</b> The lookup, the count, the decrement and the removal of
/// an entry at zero all happen under one gate. A caller counts itself before it waits for anything, so an entry can
/// never be removed and replaced under a waiter, and two callers for one key always share one entry.
/// </para>
/// <para>
/// <b>Removal.</b> <see cref="TryRemove"/> checks, under the gate, that the caller's own lease is the only one on the
/// level, and takes the entry's removal lock before it releases the gate. The removal itself runs outside the gate
/// but inside the removal lock. <see cref="Pin"/> counts itself under the gate and then passes through the same
/// removal lock before it returns, so a pin that arrives while a removal is in progress waits for it to finish and
/// only then opens or creates the level, instead of racing the removal. A pin counted before the removal's check
/// makes that check fail, and the level is left in place.
/// </para>
/// <para>
/// <b>Lock order.</b> From outermost to innermost: a <see cref="LeafLockTable"/> lease, then this table's gate, then
/// one entry's removal lock. The gate is only ever held for a few field updates. A removal lock is held only for the
/// removal's own system calls: never across an <c>await</c>, and never while taking a leaf lease, the gate or another
/// entry's removal lock. A pin waits on a removal lock with the gate released. Pins are taken while a chain is being
/// built, which always happens before its call takes a leaf lease. So no cycle can form: a removal can run while its
/// caller holds a leaf lease, but nothing that waits on a removal lock holds one.
/// </para>
/// </remarks>
internal sealed class DirectoryLevelTable
{
    private readonly Dictionary<string, Entry> _entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>The process-wide table <see cref="WorkspaceTools"/> uses unless a test gives an instance its own.</summary>
    public static DirectoryLevelTable Shared { get; } = new();

    /// <summary>How many levels currently have an entry: held by at least one chain.</summary>
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
    /// Counts the caller as a holder of <paramref name="key"/>, and returns once no removal of that level is in
    /// progress. Call this before opening or creating the level, and dispose the lease once the level is no longer
    /// pinned.
    /// </summary>
    public Lease Pin(string key)
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

        // Counted first, then wait out a removal already in progress. The remover took this lock under the gate,
        // before this caller's count, so it will not re-check the count; it finishes, and only then does this pin
        // go on to open or create the level.
        Monitor.Enter(entry.RemovalLock);
        Monitor.Exit(entry.RemovalLock);
        return new Lease(this, key, entry);
    }

    /// <summary>
    /// Runs <paramref name="remove"/> only when <paramref name="lease"/> is the one lease on its level, holding the
    /// entry's removal lock so no new pin of the level opens it meanwhile. Returns <see langword="false"/> without
    /// running it when another chain holds the level, or when the lease was already released; otherwise whatever
    /// <paramref name="remove"/> returns.
    /// </summary>
    public bool TryRemove(Lease lease, Func<bool> remove)
    {
        if (lease.Entry is not { } entry)
        {
            return false;
        }

        lock (_gate)
        {
            if (entry.References != 1)
            {
                return false;
            }

            // Cannot block: every other thread that takes this lock has counted itself first, and the count is 1.
            Monitor.Enter(entry.RemovalLock);
        }

        try
        {
            return remove();
        }
        finally
        {
            Monitor.Exit(entry.RemovalLock);
        }
    }

    private void Leave(string key, Entry entry)
    {
        lock (_gate)
        {
            entry.References--;
            if (entry.References == 0)
            {
                _entries.Remove(key);
            }
        }
    }

    internal sealed class Entry
    {
        /// <summary>Held by a removal of this level for as long as it runs; passed through by every pin.</summary>
        public object RemovalLock { get; } = new();

        /// <summary>Chains holding this level, or about to open it. Read and written only under the table's gate.</summary>
        public int References { get; set; }
    }

    /// <summary>One chain's hold on one level. Disposing it drops the reference, once.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly DirectoryLevelTable _table;
        private readonly string _key;
        private Entry? _entry;

        internal Lease(DirectoryLevelTable table, string key, Entry entry)
        {
            _table = table;
            _key = key;
            _entry = entry;
        }

        /// <summary>The entry this lease counts in, or <see langword="null"/> once disposed.</summary>
        internal Entry? Entry => Volatile.Read(ref _entry);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _entry, null) is { } entry)
            {
                _table.Leave(_key, entry);
            }
        }
    }
}
