using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Rag.NET.PgVector;
using Thalos.Memory;
using Thalos.Memory.RagNet;
using Thalos.Testing;

namespace Thalos.Tests.Memory.RagNet;

/// <summary>DI-only wiring tests (no Docker; deliberately outside the pgvector collection so the Windows CI leg runs them).</summary>
public sealed class RagNetWiringTests
{
    private const string FakeConnectionString = "Host=localhost;Username=u;Password=p;Database=d";

    internal static ServiceCollection Services(string cs, int dims, int? generatorDims, bool ensureSchema = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (generatorDims is { } g)
        {
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashedBagOfWordsEmbeddingGenerator(g));
        }

        services.AddThalos(t => t
            .UseChatClientProvider(Substitute.For<IChatClientProvider>())
            .UseInMemorySessionStore()
            .UseMemory()
            .UseRagNetMemory(o => { o.ConnectionString = cs; o.VectorDimensions = dims; o.EnsureSchemaOnStartup = ensureSchema; }));
        return services;
    }

    [Fact]
    public void Registers_the_index_and_the_initializer()
    {
        using var sp = Services(FakeConnectionString, 64, 64).BuildServiceProvider();
        sp.GetRequiredService<IMemoryIndex>().Should().BeOfType<RagNetMemoryIndex>();
        sp.GetServices<IHostedService>().Should().ContainSingle(h => h is RagNetMemorySchemaInitializer)
            .Which.Should().BeAssignableTo<IHostedLifecycleService>("the schema must exist before other hosted services start");
        using var without = Services(FakeConnectionString, 64, 64, ensureSchema: false).BuildServiceProvider();
        without.GetServices<IHostedService>().Should().NotContain(h => h is RagNetMemorySchemaInitializer);
    }

    [Fact]
    public void Rejects_missing_connection_string_or_dimensions()
    {
        var act = () => new ServiceCollection().AddThalos(t => t.UseChatClientProvider(Substitute.For<IChatClientProvider>()).UseRagNetMemory(o => o.VectorDimensions = 8));
        act.Should().Throw<ArgumentException>().WithMessage("*ConnectionString*");
        var noDims = () => new ServiceCollection().AddThalos(t => t.UseChatClientProvider(Substitute.For<IChatClientProvider>()).UseRagNetMemory(o => o.ConnectionString = FakeConnectionString));
        noDims.Should().Throw<ArgumentException>().WithMessage("*VectorDimensions*");
    }

    [Fact]
    public async Task Initializer_fails_fast_when_generator_and_configured_dimensions_differ()
    {
        using var sp = Services(FakeConnectionString, 64, 32).BuildServiceProvider();
        var init = sp.GetServices<IHostedService>().OfType<RagNetMemorySchemaInitializer>().Single();
        var act = () => init.StartingAsync(default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*VectorDimensions*32*");
        var noop = () => init.StartAsync(default);
        await noop.Should().NotThrowAsync("StartAsync is a no-op; the work happens in StartingAsync");
    }

    [Fact]
    public void Calling_UseRagNetMemory_twice_is_last_wins_with_a_single_initializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashedBagOfWordsEmbeddingGenerator(128));
        services.AddThalos(t => t
            .UseChatClientProvider(Substitute.For<IChatClientProvider>())
            .UseInMemorySessionStore()
            .UseMemory()
            .UseRagNetMemory(FakeConnectionString, 64)
            .UseRagNetMemory(o => { o.ConnectionString = FakeConnectionString + ";Application Name=second"; o.VectorDimensions = 128; }));

        services.Count(d => d.ServiceType == typeof(PgVectorStore) && Equals(d.ServiceKey, RagNetMemory.VectorStoreKey)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(RagNetMemoryOptions)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RagNetMemorySchemaInitializer)).Should().Be(1);

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<RagNetMemoryOptions>().VectorDimensions.Should().Be(128);
        sp.GetRequiredService<RagNetMemoryOptions>().ConnectionString.Should().EndWith("Application Name=second");
        sp.GetRequiredService<IMemoryIndex>().Should().BeOfType<RagNetMemoryIndex>().Which.Options.VectorDimensions.Should().Be(128);
        sp.GetServices<IHostedService>().Should().ContainSingle(h => h is RagNetMemorySchemaInitializer);
        sp.GetRequiredKeyedService<PgVectorStore>(RagNetMemory.VectorStoreKey).Should().NotBeNull();

        // a later call that turns the schema step off removes the initializer
        var off = new ServiceCollection();
        off.AddThalos(t => t.UseChatClientProvider(Substitute.For<IChatClientProvider>()).UseMemory()
            .UseRagNetMemory(FakeConnectionString, 64)
            .UseRagNetMemory(o => { o.ConnectionString = FakeConnectionString; o.VectorDimensions = 64; o.EnsureSchemaOnStartup = false; }));
        off.Should().NotContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RagNetMemorySchemaInitializer));
    }

    [Fact]
    public void UseMemory_after_UseRagNetMemory_still_resolves_the_RagNet_index()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashedBagOfWordsEmbeddingGenerator(64));
        services.AddThalos(t => t
            .UseChatClientProvider(Substitute.For<IChatClientProvider>())
            .UseInMemorySessionStore()
            .UseRagNetMemory(FakeConnectionString, 64)
            .UseMemory());
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IMemoryIndex>().Should().BeOfType<RagNetMemoryIndex>();
    }

    [Fact]
    public void Without_a_generator_the_index_is_unavailable_and_the_initializer_still_registers()
    {
        // §0.6 item 13: hosts without an IEmbeddingGenerator (Daedalus without Ollama) must still start
        using var sp = Services(FakeConnectionString, 64, generatorDims: null).BuildServiceProvider();
        sp.GetRequiredService<IMemoryIndex>().Should().BeSameAs(UnavailableMemoryIndex.Instance);
        sp.GetRequiredService<IMemoryIndex>().Should().BeSameAs(UnavailableMemoryIndex.Instance, "the singleton factory resolves once");
        sp.GetServices<IHostedService>().Should().ContainSingle(h => h is RagNetMemorySchemaInitializer);
        sp.GetRequiredKeyedService<PgVectorStore>(RagNetMemory.VectorStoreKey).Should().NotBeNull();
    }
}

[Collection(PgVectorCollection.Name)]
[Trait("Category", "Docker")]
public sealed class RagNetWiringDockerTests(PgVectorFixture pg)
{
    [Fact]
    public async Task Initializer_creates_the_schema_and_fails_on_a_table_dimension_mismatch()
    {
        await pg.ResetAsync();

        using (var sp = RagNetWiringTests.Services(pg.ConnectionString, 64, 64).BuildServiceProvider())
        {
            await sp.GetServices<IHostedService>().OfType<RagNetMemorySchemaInitializer>().Single().StartingAsync(default);
            var svc = sp.GetRequiredService<IMemoryService>();
            var r = await svc.RememberAsync(new RememberRequest { OwnerId = "alice", Text = "papa quebec romeo" }, default);
            r.Value.IndexPending.Should().BeFalse();
            (await svc.RecallAsync("papa quebec romeo", new MemoryScope("alice", null), new RecallOptions { MinScore = 0.5 }, default)).Value.Should().ContainSingle();
        }

        using var mismatched = RagNetWiringTests.Services(pg.ConnectionString, 128, 128).BuildServiceProvider();
        var act = () => mismatched.GetServices<IHostedService>().OfType<RagNetMemorySchemaInitializer>().Single().StartingAsync(default);
        (await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Thalos.NET.Memory.RagNet*vector(N) mismatch*ReindexAsync*"))
            .Which.Message.Should().Contain("vector(64)").And.Contain("vector(128)", "Rag.NET's own message is kept");

        // other test classes reset (drop) the table before initialising, but leave the shared default behind anyway
        await pg.ResetAsync();
    }

    [Fact]
    public async Task Initializer_without_a_generator_still_creates_the_schema_at_the_configured_dimensions()
    {
        await pg.ResetAsync();
        using var sp = RagNetWiringTests.Services(pg.ConnectionString, 64, generatorDims: null).BuildServiceProvider();
        await sp.GetServices<IHostedService>().OfType<RagNetMemorySchemaInitializer>().Single().StartingAsync(default);

        using var same = new PgVectorStore(pg.ConnectionString, 64);
        var ok = () => same.InitializeAsync();
        await ok.Should().NotThrowAsync("the table was created with vector(64)");
        using var other = new PgVectorStore(pg.ConnectionString, 128);
        var mismatch = () => other.InitializeAsync();
        await mismatch.Should().ThrowAsync<InvalidOperationException>();

        // remember still works (stores as IndexPending) — a later reindex with a generator can fill the table
        var r = await sp.GetRequiredService<IMemoryService>().RememberAsync(new RememberRequest { OwnerId = "alice", Text = "sierra tango uniform" }, default);
        r.IsSuccess.Should().BeTrue();
        r.Value.IndexPending.Should().BeTrue();
        await pg.ResetAsync();
    }

    [Fact]
    public async Task Second_UseRagNetMemory_call_decides_the_table_dimensions()
    {
        await pg.ResetAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashedBagOfWordsEmbeddingGenerator(128));
        services.AddThalos(t => t.UseChatClientProvider(Substitute.For<IChatClientProvider>()).UseInMemorySessionStore().UseMemory()
            .UseRagNetMemory(pg.ConnectionString, 64)
            .UseRagNetMemory(pg.ConnectionString, 128));
        using var sp = services.BuildServiceProvider();
        await sp.GetServices<IHostedService>().OfType<RagNetMemorySchemaInitializer>().Single().StartingAsync(default);

        using var at128 = new PgVectorStore(pg.ConnectionString, 128);
        var ok = () => at128.InitializeAsync();
        await ok.Should().NotThrowAsync("the keyed store was built from the second call's options");
        using var at64 = new PgVectorStore(pg.ConnectionString, 64);
        var mismatch = () => at64.InitializeAsync();
        await mismatch.Should().ThrowAsync<InvalidOperationException>();
        await pg.ResetAsync();
    }
}
