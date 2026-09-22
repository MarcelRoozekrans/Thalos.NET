using System.Collections.Concurrent;
using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// An <see cref="IProcessDefinitionStore"/> decorator that keeps the parsed <see cref="ProcessDefinition"/> for a
/// <c>(process, version)</c> pair in memory, so run-time resolution does not re-read and re-parse the same YAML on
/// every node dispatch and every resume.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a decorator, and not a field on each caller.</b> Both places that resolve a definition at run time —
/// <see cref="WorkflowNodeDispatcher"/> and the ORM store's <c>ResumeAsync</c> — take an
/// <see cref="IProcessDefinitionStore"/>, so registering this as <em>the</em> <see cref="IProcessDefinitionStore"/>
/// singleton gives both of them one shared cache from one composition-root decision. A cache field on each caller
/// instead would be two caches of the same fact, populated independently and evicted independently, which is the
/// second source of truth this whole task exists to remove. Keeping it here rather than inside
/// <c>OrmProcessDefinitionStore</c> also keeps it backend-agnostic: nothing in it is PostgreSQL-shaped.
/// </para>
/// <para>
/// <b>Why caching is sound.</b> A run pins <c>(process, version)</c> at <c>IWorkflowStore.StartAsync</c> and never
/// changes it, and <see cref="IProcessDefinitionStore.UpsertAndActivateAsync"/> makes a new version current rather
/// than editing a live one, so an entry keyed on a pinned pair describes a graph that does not move underneath it.
/// This decorator does not rely on that alone, though: it sits on the write path too, and drops the affected key on
/// every <see cref="UpsertAndActivateAsync"/> and every successful <see cref="TryRemoveAsync"/>. That matters
/// because the underlying upsert is an <em>upsert</em> — re-syncing a process file without bumping its version
/// rewrites that version's stored YAML in place — so "immutable version" is a convention the sync discipline keeps,
/// not something the storage layer enforces. Write-through eviction makes this cache correct even when that
/// convention is broken in-process; see this type's note on the limit of that.
/// </para>
/// <para>
/// <b>What it does not protect against.</b> Eviction is per instance, so it cannot see a same-version rewrite
/// performed by a <em>different</em> host process. Two hosts sharing one database, where one re-activates version N
/// with changed YAML, can leave the other serving the previous parse of version N until that key is evicted for
/// some other reason. Bumping the version — the discipline the pin exists to support — is what actually prevents
/// that; this decorator narrows the window rather than closing it.
/// </para>
/// <para>
/// <b>What bounds its growth.</b> Two things. Entries are only ever added for a pair a caller actually asked for,
/// so the live working set is the set of versions non-terminal runs still pin — small by construction, and shrunk
/// further by the retention rule that removes versions nothing pins. On top of that the map is hard-capped at
/// <see cref="Capacity"/> entries: once over, the oldest-inserted entries are dropped until it is back at the cap.
/// Insertion order, not recency, is the eviction policy on purpose — every entry costs the same single indexed read
/// plus parse to rebuild, so the bookkeeping an LRU would add buys nothing here. The cap is a backstop against a
/// host that churns versions indefinitely, not the mechanism expected to do the work.
/// </para>
/// <para>
/// <b>Failures are never cached.</b> A definition that does not resolve is left out of the map entirely, so a
/// dispatch that raced ahead of the sync that stores its definition fails that one run and then resolves normally
/// once the row lands. Caching the miss would turn a transient ordering accident into a permanently unrunnable
/// process for the lifetime of the host.
/// </para>
/// </remarks>
public sealed class CachingProcessDefinitionStore : IProcessDefinitionStore
{
    /// <summary>The default <see cref="Capacity"/>: far above any realistic count of versions live runs pin at once, so the cap never evicts a working entry in practice.</summary>
    public const int DefaultCapacity = 256;

    private readonly ConcurrentDictionary<ProcessVersion, Entry> _entries = new(ProcessVersionComparer.Instance);
    private readonly IProcessDefinitionStore _inner;
    private readonly object _trimGate = new();
    private long _stamp;

    /// <summary>Wraps <paramref name="inner"/>, caching what its <see cref="IProcessDefinitionStore.GetAsync"/> resolves.</summary>
    /// <param name="inner">The store actually holding the definitions.</param>
    /// <param name="capacity">The maximum number of parsed definitions held at once. Must be positive.</param>
    public CachingProcessDefinitionStore(IProcessDefinitionStore inner, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _inner = inner;
        Capacity = capacity;
    }

    /// <summary>The maximum number of parsed definitions held at once — see this type's remarks on what bounds growth.</summary>
    public int Capacity { get; }

    /// <summary>The number of definitions currently cached. Exposed for tests that assert the cap actually bounds the map.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc/>
    public async ValueTask UpsertAndActivateAsync(ProcessDefinition definition, string yaml, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);

        await _inner.UpsertAndActivateAsync(definition, yaml, ct).ConfigureAwait(false);

        // The write path is an upsert, so this key's stored YAML may have just been rewritten in place. Dropping
        // the entry after the write costs one re-read on the next resolve and removes the only in-process way a
        // cached parse could disagree with the row behind it.
        _entries.TryRemove(new ProcessVersion(definition.Name, definition.Version), out _);
    }

    /// <inheritdoc/>
    public ValueTask<int?> GetActiveVersionAsync(string process, CancellationToken ct) =>
        // Deliberately not cached: which version is *active* is exactly the fact a sync changes, and it is read
        // when starting a run rather than per node dispatch, so there is no hot path here to protect and a stale
        // answer would start new runs on a superseded version.
        _inner.GetActiveVersionAsync(process, ct);

    /// <inheritdoc/>
    public async ValueTask<Result<ProcessDefinition>> GetAsync(string process, int version, CancellationToken ct)
    {
        var key = new ProcessVersion(process, version);
        if (_entries.TryGetValue(key, out var cached))
        {
            return Result<ProcessDefinition>.Success(cached.Definition);
        }

        var loaded = await _inner.GetAsync(process, version, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        _entries[key] = new Entry(loaded.Value, Interlocked.Increment(ref _stamp));
        TrimToCapacity();
        return loaded;
    }

    /// <inheritdoc/>
    public async ValueTask<Result> TryRemoveAsync(string process, int version, CancellationToken ct)
    {
        var result = await _inner.TryRemoveAsync(process, version, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            // Only on success: a removal refused because a run still pins the version leaves the row — and so the
            // cached parse of it — perfectly valid, and evicting there would throw away a hot entry for nothing.
            _entries.TryRemove(new ProcessVersion(process, version), out _);
        }

        return result;
    }

    /// <summary>
    /// Drops oldest-inserted entries until the map is back within <see cref="Capacity"/>. Held under a gate so two
    /// concurrent resolves cannot each compute an eviction set from the same over-capacity snapshot and between
    /// them evict roughly twice as many entries as either intended.
    /// </summary>
    /// <remarks>
    /// A linear scan per evicted entry rather than a sorted projection: this runs only on the insert that pushes
    /// the map past <see cref="Capacity"/>, and evicts one entry when it does, so the scan is over a map already
    /// bounded at the cap and is not on any path a normal resolve takes. Written as plain loops because the
    /// allocation analyzer — correctly — refuses LINQ that materialises inside a loop.
    /// </remarks>
    private void TrimToCapacity()
    {
        if (_entries.Count <= Capacity)
        {
            return;
        }

        lock (_trimGate)
        {
            var excess = _entries.Count - Capacity;
            for (var evicted = 0; evicted < excess; evicted++)
            {
                ProcessVersion oldestKey = default;
                var oldestStamp = long.MaxValue;
                var found = false;
                foreach (var pair in _entries)
                {
                    if (pair.Value.Stamp >= oldestStamp)
                    {
                        continue;
                    }

                    oldestStamp = pair.Value.Stamp;
                    oldestKey = pair.Key;
                    found = true;
                }

                if (!found)
                {
                    return;
                }

                _entries.TryRemove(oldestKey, out _);
            }
        }
    }

    private readonly record struct ProcessVersion(string Process, int Version);

    private readonly record struct Entry(ProcessDefinition Definition, long Stamp);

    /// <summary>Ordinal comparison on the process name — process names are identifiers from a file, never culture-sensitive text.</summary>
    private sealed class ProcessVersionComparer : IEqualityComparer<ProcessVersion>
    {
        public static readonly ProcessVersionComparer Instance = new();

        public bool Equals(ProcessVersion x, ProcessVersion y) =>
            x.Version == y.Version && string.Equals(x.Process, y.Process, StringComparison.Ordinal);

        public int GetHashCode(ProcessVersion obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Process), obj.Version);
    }
}
