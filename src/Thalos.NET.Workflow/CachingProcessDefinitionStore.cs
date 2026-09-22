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
/// changes it, and <see cref="IProcessDefinitionStore.UpsertAndActivateAsync"/> <em>refuses</em> to store a version
/// that already exists with different content, so a cached entry keyed on a pinned pair describes a graph that
/// cannot move underneath it. That is an enforced invariant at the storage layer, not a convention: a same-version
/// edit is an error telling the author to bump the version, and nothing rewrites a stored definition in place.
/// </para>
/// <para>
/// <b>Eviction is kept anyway.</b> This decorator still drops the affected key on a successful
/// <see cref="UpsertAndActivateAsync"/> and a successful <see cref="TryRemoveAsync"/>. It is no longer
/// load-bearing — the invariant above is what makes staleness impossible, including across host processes, which
/// per-instance eviction could never have covered — but keeping it means the cache is correct on its own terms
/// rather than only in combination with a rule enforced elsewhere.
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

    /// <summary>
    /// The single-flight map: at most one in-flight read per key, so N concurrent misses on the same version
    /// collapse to one call into <see cref="_inner"/> instead of N. See <see cref="GetAsync"/> for why that
    /// matters rather than being a micro-optimisation.
    /// </summary>
    private readonly ConcurrentDictionary<ProcessVersion, Lazy<Task<Result<ProcessDefinition>>>> _inFlight =
        new(ProcessVersionComparer.Instance);

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
    public async ValueTask<Result> UpsertAndActivateAsync(ProcessDefinition definition, string yaml, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var result = await _inner.UpsertAndActivateAsync(definition, yaml, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            // Belt and braces rather than load-bearing: the store now refuses a same-version content change, so a
            // successful write either inserted a version this cache never held or rewrote one to itself. Dropping
            // the key anyway costs one re-read and means this decorator stays correct on its own terms rather than
            // on an invariant enforced somewhere else. Not dropped on failure — nothing was written, so the cached
            // parse still matches the row.
            _entries.TryRemove(new ProcessVersion(definition.Name, definition.Version), out _);
        }

        return result;
    }

    /// <inheritdoc/>
    public ValueTask<int?> GetActiveVersionAsync(string process, CancellationToken ct) =>
        // Deliberately not cached: which version is *active* is exactly the fact a sync changes, and it is read
        // when starting a run rather than per node dispatch, so there is no hot path here to protect and a stale
        // answer would start new runs on a superseded version.
        _inner.GetActiveVersionAsync(process, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Single-flight.</b> A cache only helps once something is in it; the dangerous moment is a cold one. On a
    /// host that has just started, every parked gate can resume at once, and every one of those resumes misses on
    /// the same handful of versions. Without this, N concurrent misses on one key are N reads. That is worse than
    /// wasted work in this specific composition: the ORM store's <c>ResumeAsync</c> holds its run-transaction
    /// connection while asking this store for a definition, which takes a <em>second</em> connection from the same
    /// pool — so a stampede has every resume holding connection A and queuing for connection B, and at high enough
    /// concurrency they block until the pool timeout fires rather than merely being slow. Collapsing to one
    /// in-flight read per key caps second-connection demand at one per distinct version no matter how many resumes
    /// arrive together, which fixes that at its source.
    /// </para>
    /// <para>
    /// <b><see cref="Lazy{T}"/>, not a bare task.</b> <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey, Func{TKey,TValue})"/>
    /// does not promise the factory runs only once under contention — it promises only that one result wins. A
    /// bare task factory could therefore still start two reads and discard one, which is exactly what this is
    /// supposed to prevent. <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> gives the once-only
    /// guarantee the map itself does not.
    /// </para>
    /// <para>
    /// <b>The shared read is not cancellable.</b> It runs with <see cref="CancellationToken.None"/> deliberately:
    /// the token belonging to whichever caller happened to arrive first must not be able to cancel a read every
    /// other waiter is depending on. Each caller instead observes its own <paramref name="ct"/> through
    /// <see cref="Task.WaitAsync(CancellationToken)"/>, so cancelling one caller detaches that caller and leaves
    /// the shared read running for the rest. The cost is that a cancelled or shutting-down host may wait out one
    /// single-row indexed read; that is a far smaller price than a cancellation racing across unrelated callers.
    /// </para>
    /// </remarks>
    public async ValueTask<Result<ProcessDefinition>> GetAsync(string process, int version, CancellationToken ct)
    {
        var key = new ProcessVersion(process, version);
        if (_entries.TryGetValue(key, out var cached))
        {
            return Result<ProcessDefinition>.Success(cached.Definition);
        }

        var flight = _inFlight.GetOrAdd(
            key,
            static (k, self) => new Lazy<Task<Result<ProcessDefinition>>>(
                () => self.ResolveAsync(k), LazyThreadSafetyMode.ExecutionAndPublication),
            this);

        try
        {
            return await flight.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // Removed by key *and* instance: a later miss on the same key may already have installed a fresh
            // flight by the time this one finishes, and removing by key alone would evict that newer read's entry
            // and let the next arrival start a third.
            _inFlight.TryRemove(new KeyValuePair<ProcessVersion, Lazy<Task<Result<ProcessDefinition>>>>(key, flight));
        }
    }

    /// <summary>
    /// The one read every waiter on a key shares. Populates the cache before returning, so all of them are served
    /// by the single inner call rather than the first one populating and the rest still having read.
    /// </summary>
    private async Task<Result<ProcessDefinition>> ResolveAsync(ProcessVersion key)
    {
        var loaded = await _inner.GetAsync(key.Process, key.Version, CancellationToken.None).ConfigureAwait(false);
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
