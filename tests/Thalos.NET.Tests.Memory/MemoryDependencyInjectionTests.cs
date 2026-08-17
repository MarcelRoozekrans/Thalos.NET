using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Thalos.Memory;
using Thalos.Runtime;
using Thalos.Testing;

namespace Thalos.Tests.Memory;

public sealed class MemoryDependencyInjectionTests
{
    private static ServiceProvider Build(Action<ThalosBuilder>? extra = null, bool withEmbeddings = true, Action<MemoryOptions>? configure = null)
    {
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake"); provider.DefaultModel.Returns("m");
        var services = new ServiceCollection().AddLogging();
        if (withEmbeddings)
        {
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashedBagOfWordsEmbeddingGenerator());
        }

        services.AddThalos(t =>
        {
            t.UseChatClientProvider(provider).UseInMemorySessionStore().UseMemory(configure ?? (o => o.SharedOwnerId = "daedalus"));
            extra?.Invoke(t);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolves_service_store_index_tool_source_and_context_source()
    {
        using var sp = Build();
        sp.GetRequiredService<IMemoryService>().Should().BeOfType<MemoryService>();
        sp.GetRequiredService<IMemoryStore>().Should().BeOfType<MemoryStoreInstrumented>();
        sp.GetRequiredService<IMemoryIndex>().Should().BeOfType<InMemoryMemoryIndex>();
        sp.GetServices<IToolSource>().Should().ContainSingle(s => s.Name == "memory");
        sp.GetServices<IAgentContextProviderSource>().Should().ContainSingle().Which.Should().BeOfType<MemoryContextProviderSource>();
        sp.GetRequiredService<IOptions<MemoryOptions>>().Value.SharedOwnerId.Should().Be("daedalus");
    }

    [Fact]
    public void Without_an_embedding_generator_the_index_is_unavailable()
    {
        using var sp = Build(withEmbeddings: false);
        sp.GetRequiredService<IMemoryIndex>().Should().BeSameAs(UnavailableMemoryIndex.Instance);
    }

    [Fact]
    public async Task Custom_store_and_index_replace_the_defaults_in_any_order()
    {
        using var before = Build(t => t.UseMemoryStore<FakeStore>().UseMemoryIndex<FakeIndex>());
        await AssertFakeStoreIsWrapped(before);
        before.GetRequiredService<IMemoryIndex>().Should().BeOfType<FakeIndex>();

        var services = new ServiceCollection().AddLogging();
        var provider = Substitute.For<IChatClientProvider>();
        services.AddThalos(t => t.UseChatClientProvider(provider).UseInMemorySessionStore().UseMemoryIndex<FakeIndex>().UseMemoryStore<FakeStore>().UseMemory());
        using var after = services.BuildServiceProvider();
        await AssertFakeStoreIsWrapped(after);
        after.GetRequiredService<IMemoryIndex>().Should().BeOfType<FakeIndex>();
    }

    /// <summary>The telemetry proxy must wrap the custom store, not the default one: a record written through IMemoryStore is visible in FakeStore.</summary>
    private static async Task AssertFakeStoreIsWrapped(ServiceProvider sp)
    {
        var store = sp.GetRequiredService<IMemoryStore>();
        store.Should().BeOfType<MemoryStoreInstrumented>();
        var now = DateTimeOffset.UtcNow;
        var record = new MemoryRecord { Id = MemoryId.New(), OwnerId = "alice", Kind = MemoryKind.Fact, Text = "written through the proxy", CreatedAt = now, UpdatedAt = now };
        (await store.CreateAsync(record, default)).IsSuccess.Should().BeTrue();
        (await sp.GetRequiredService<FakeStore>().GetAsync(record.Id, default)).IsSuccess.Should().BeTrue("the proxy delegates to FakeStore");
    }

    [Fact]
    public void Binds_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Memory:SharedOwnerId"] = "cfg", ["Thalos:Memory:Recall:TopK"] = "3", ["Thalos:Memory:Dedupe:Threshold"] = "0.9", ["Thalos:Memory:ExposeTools"] = "false",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.UseChatClientProvider(Substitute.For<IChatClientProvider>()).UseInMemorySessionStore().UseMemory(config));
        using var sp = services.BuildServiceProvider();
        var o = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
        o.SharedOwnerId.Should().Be("cfg"); o.Recall.TopK.Should().Be(3); o.Dedupe.Threshold.Should().Be(0.9); o.ExposeTools.Should().BeFalse();
    }

    [Fact]
    public void UseMemory_is_idempotent_and_the_last_configure_wins()
    {
        using var sp = Build(t => t.UseMemory(o => o.SharedOwnerId = "second"));
        sp.GetServices<IToolSource>().Should().ContainSingle(s => s.Name == "memory");
        sp.GetServices<IAgentContextProviderSource>().Should().ContainSingle();
        sp.GetServices<IMemoryService>().Should().ContainSingle();
        sp.GetRequiredService<IOptions<MemoryOptions>>().Value.SharedOwnerId.Should().Be("second");
    }

    [Fact]
    public void A_whitespace_shared_owner_is_normalised_to_null()
    {
        using var sp = Build(configure: o => o.SharedOwnerId = "   ");
        sp.GetRequiredService<IOptions<MemoryOptions>>().Value.SharedOwnerId.Should().BeNull();
    }

    [Theory]
    [InlineData(0.0)] [InlineData(1.5)] [InlineData(-0.1)] [InlineData(double.NaN)]
    public void An_invalid_dedupe_threshold_fails_option_validation(double threshold)
    {
        using var sp = Build(configure: o => o.Dedupe.Threshold = threshold);
        var act = () => sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*Dedupe.Threshold*");
    }

    [Fact]
    public void A_disabled_dedupe_ignores_the_threshold()
    {
        using var sp = Build(configure: o => { o.Dedupe.Enabled = false; o.Dedupe.Threshold = 0; });
        sp.GetRequiredService<IOptions<MemoryOptions>>().Value.Dedupe.Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)] [InlineData(-3)]
    public void A_non_positive_recall_TopK_fails_option_validation(int topK)
    {
        using var sp = Build(configure: o => o.Recall.TopK = topK);
        var act = () => sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*Recall.TopK*");
    }

    [Theory]
    [InlineData(-0.1)] [InlineData(1.1)] [InlineData(double.NaN)]
    public void A_recall_MinScore_outside_zero_to_one_fails_option_validation(double minScore)
    {
        using var sp = Build(configure: o => o.Recall.MinScore = minScore);
        var act = () => sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*Recall.MinScore*");
    }

    private sealed class FakeStore : IMemoryStore
    {
        private readonly InMemoryMemoryStore _inner = new(TimeProvider.System);
        public ValueTask<ZeroAlloc.Results.Result<MemoryRecord, AgentError>> CreateAsync(MemoryRecord record, CancellationToken ct) => _inner.CreateAsync(record, ct);
        public ValueTask<ZeroAlloc.Results.Result<MemoryRecord, AgentError>> GetAsync(MemoryId id, CancellationToken ct) => _inner.GetAsync(id, ct);
        public ValueTask<ZeroAlloc.Results.Result<MemoryRecord, AgentError>> UpdateAsync(MemoryId id, MemoryUpdate update, CancellationToken ct) => _inner.UpdateAsync(id, update, ct);
        public ValueTask<ZeroAlloc.Results.UnitResult<AgentError>> DeleteAsync(MemoryId id, CancellationToken ct) => _inner.DeleteAsync(id, ct);
        public ValueTask<ZeroAlloc.Results.Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct) => _inner.ListAsync(query, ct);
        public ValueTask<ZeroAlloc.Results.UnitResult<AgentError>> MarkRecalledAsync(IReadOnlyList<MemoryId> ids, DateTimeOffset at, CancellationToken ct) => _inner.MarkRecalledAsync(ids, at, ct);
        public IAsyncEnumerable<MemoryRecord> StreamAsync(MemoryQuery query, CancellationToken ct) => _inner.StreamAsync(query, ct);
    }

    private sealed class FakeIndex : IMemoryIndex
    {
        private readonly UnavailableMemoryIndex _inner = UnavailableMemoryIndex.Instance;
        public ValueTask<ZeroAlloc.Results.UnitResult<AgentError>> UpsertAsync(IReadOnlyList<MemoryRecord> records, CancellationToken ct) => _inner.UpsertAsync(records, ct);
        public ValueTask<ZeroAlloc.Results.Result<IReadOnlyList<MemoryHit>, AgentError>> SearchAsync(string query, MemoryScope scope, MemorySearchOptions options, CancellationToken ct) => _inner.SearchAsync(query, scope, options, ct);
        public ValueTask<ZeroAlloc.Results.UnitResult<AgentError>> RemoveAsync(MemoryId id, CancellationToken ct) => _inner.RemoveAsync(id, ct);
        public ValueTask<ZeroAlloc.Results.Result<MemoryIndexHealth, AgentError>> ProbeAsync(CancellationToken ct) => _inner.ProbeAsync(ct);
    }
}
