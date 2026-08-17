using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Thalos.Memory;
using Thalos.Runtime;
using Thalos.Testing;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Memory;

internal sealed class TestCaller(string id, params string[] roles) : ISecurityContext
{
    public string Id { get; } = id;
    public IReadOnlySet<string> Roles { get; } = roles.ToHashSet(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Real store, real cosine index over the bag-of-words generator, real hub — swap the index to test degradation.</summary>
internal sealed class MemoryServiceFixture
{
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
    public InMemoryMemoryStore Store { get; }
    public IMemoryIndex Index { get; set; }
    public MemoryOptions Options { get; } = new();
    public AgentEventHub Hub { get; } = new();
    public List<AgentEvent> HubEvents { get; } = [];

    public MemoryServiceFixture(IMemoryIndex? index = null)
    {
        Store = new InMemoryMemoryStore(Clock);
        Index = index ?? new InMemoryMemoryIndex(new HashedBagOfWordsEmbeddingGenerator());
        Hub.Subscribe((e, _) => { HubEvents.Add(e); return default; });
    }

    public MemoryService Build() => new(Store, Index, Microsoft.Extensions.Options.Options.Create(Options), Clock, Hub);

    public static RememberRequest Remember(string text, string owner = "alice", AgentId? agent = null, MemoryKind? kind = null, double importance = 0.5, params string[] tags) =>
        new() { OwnerId = owner, AgentId = agent, Text = text, Kind = kind ?? MemoryKind.Fact, Importance = importance, Tags = tags, Source = "test" };
}
