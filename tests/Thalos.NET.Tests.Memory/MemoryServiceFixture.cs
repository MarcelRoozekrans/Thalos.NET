using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Thalos.Memory;
using Thalos.Runtime;
using Thalos.Testing;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Memory;

internal sealed class TestCaller(string id, params string[] roles) : ISecurityContext
{
    public string Id { get; } = id;
    public IReadOnlySet<string> Roles { get; } = roles.ToHashSet(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Real store, real cosine index over the bag-of-words generator, real hub — swap the index or wrap the store to test degradation.</summary>
internal sealed class MemoryServiceFixture
{
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
    public InMemoryMemoryStore Store { get; }
    public IMemoryIndex Index { get; set; }
    public MemoryOptions Options { get; } = new();
    public AgentEventHub Hub { get; } = new();
    public List<AgentEvent> HubEvents { get; } = [];
    public CapturingLogger Logger { get; } = new();

    public MemoryServiceFixture(IMemoryIndex? index = null)
    {
        Store = new InMemoryMemoryStore(Clock);
        Index = index ?? new InMemoryMemoryIndex(new HashedBagOfWordsEmbeddingGenerator());
        Hub.Subscribe((e, _) => { HubEvents.Add(e); return default; });
    }

    /// <summary>Builds the service over <see cref="Store"/> (or <paramref name="store"/> when given, e.g. a <see cref="HookedStore"/> around it).</summary>
    public MemoryService Build(IMemoryStore? store = null) => new(store ?? Store, Index, Microsoft.Extensions.Options.Options.Create(Options), Clock, Hub, Logger);

    public static RememberRequest Remember(string text, string owner = "alice", AgentId? agent = null, MemoryKind? kind = null, double importance = 0.5, params string[] tags) =>
        new() { OwnerId = owner, AgentId = agent, Text = text, Kind = kind ?? MemoryKind.Fact, Importance = importance, Tags = tags, Source = "test" };
}

/// <summary>Records every log call's event id (the service's <c>[LoggerMessage]</c> ids) so tests can assert "logged, not fatal".</summary>
internal sealed class CapturingLogger : ILogger<MemoryService>
{
    public List<(int EventId, LogLevel Level)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, logLevel));
}

/// <summary>Delegates to a real store; a hook returning an <see cref="AgentError"/> makes that call fail instead (null = pass through).</summary>
internal sealed class HookedStore(IMemoryStore inner) : IMemoryStore
{
    public Func<MemoryId, AgentError?>? OnGet { get; set; }
    public Func<MemoryId, MemoryUpdate, AgentError?>? OnUpdate { get; set; }
    public Func<AgentError?>? OnMarkRecalled { get; set; }

    public ValueTask<Result<MemoryRecord, AgentError>> CreateAsync(MemoryRecord record, CancellationToken ct) => inner.CreateAsync(record, ct);

    public ValueTask<Result<MemoryRecord, AgentError>> GetAsync(MemoryId id, CancellationToken ct) =>
        OnGet?.Invoke(id) is { } error ? new(Result<MemoryRecord, AgentError>.Failure(error)) : inner.GetAsync(id, ct);

    public ValueTask<Result<MemoryRecord, AgentError>> UpdateAsync(MemoryId id, MemoryUpdate update, CancellationToken ct) =>
        OnUpdate?.Invoke(id, update) is { } error ? new(Result<MemoryRecord, AgentError>.Failure(error)) : inner.UpdateAsync(id, update, ct);

    public ValueTask<UnitResult<AgentError>> DeleteAsync(MemoryId id, CancellationToken ct) => inner.DeleteAsync(id, ct);
    public ValueTask<Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct) => inner.ListAsync(query, ct);

    public ValueTask<UnitResult<AgentError>> MarkRecalledAsync(IReadOnlyList<MemoryId> ids, DateTimeOffset at, CancellationToken ct) =>
        OnMarkRecalled?.Invoke() is { } error ? new(UnitResult<AgentError>.Failure(error)) : inner.MarkRecalledAsync(ids, at, ct);

    public IAsyncEnumerable<MemoryRecord> StreamAsync(MemoryQuery query, CancellationToken ct) => inner.StreamAsync(query, ct);
}
