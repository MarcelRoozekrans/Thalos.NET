using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Tests for <see cref="CachingProcessDefinitionStore"/>: the two things a cache in front of definition
/// resolution has to get right are that it actually saves the read, and that it never serves an answer the
/// underlying store would no longer give. Every assertion here is written against
/// <see cref="InMemoryProcessDefinitionStore.GetCallCount"/> rather than the returned value where the point is
/// "did this reach the store", because a cache that returns the right answer while re-reading every time is
/// indistinguishable from no cache at all by value alone.
/// </summary>
public sealed class CachingProcessDefinitionStoreTests
{
    private static string Yaml(string process, int version, string next) => $$"""
        process: {{process}}
        version: {{version}}
        nodes:
          implement: { agent: backend, skill: tdd, next: {{next}} }
          {{next}}: { terminal: succeeded }
        """;

    [Fact]
    public async Task A_second_resolve_of_the_same_version_does_not_reach_the_inner_store()
    {
        var inner = new InMemoryProcessDefinitionStore().Seed(Yaml("pipeline", 1, "done"));
        var cache = new CachingProcessDefinitionStore(inner);

        var first = await cache.GetAsync("pipeline", 1, CancellationToken.None);
        var second = await cache.GetAsync("pipeline", 1, CancellationToken.None);

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error : "");
        second.IsSuccess.Should().BeTrue();
        second.Value.Nodes.Should().ContainKey("done");
        inner.GetCallCount.Should().Be(1, "the second resolve must be served from the cache — a count of 2 means the cache is not caching");
    }

    [Fact]
    public async Task Two_versions_of_one_process_are_cached_separately()
    {
        var inner = new InMemoryProcessDefinitionStore()
            .Seed(Yaml("pipeline", 1, "done"))
            .Seed(Yaml("pipeline", 2, "audit"));
        var cache = new CachingProcessDefinitionStore(inner);

        var v1 = await cache.GetAsync("pipeline", 1, CancellationToken.None);
        var v2 = await cache.GetAsync("pipeline", 2, CancellationToken.None);

        v1.Value.Nodes.Should().ContainKey("done").And.NotContainKey("audit");
        v2.Value.Nodes.Should().ContainKey("audit", "a cache keyed on the process name alone would hand version 2's resolve version 1's graph");
        cache.Count.Should().Be(2);
    }

    /// <summary>
    /// The premise the cache rests on is that a pinned version does not change under it. The storage layer does
    /// not actually enforce that — the upsert rewrites a version's YAML in place — so the cache drops the key it
    /// just wrote. Turns red if that eviction is removed: the resolve afterwards would return the stale graph.
    /// </summary>
    [Fact]
    public async Task Re_activating_a_version_drops_its_cached_entry()
    {
        var inner = new InMemoryProcessDefinitionStore().Seed(Yaml("pipeline", 1, "done"));
        var cache = new CachingProcessDefinitionStore(inner);

        (await cache.GetAsync("pipeline", 1, CancellationToken.None)).Value.Nodes.Should().ContainKey("done");

        // Same version number, different graph — the case "an immutable pinned version" does not cover.
        var rewritten = Yaml("pipeline", 1, "audit");
        var parsed = ProcessLoader.Load(rewritten);
        parsed.IsSuccess.Should().BeTrue(parsed.IsFailure ? parsed.Error : "");
        await cache.UpsertAndActivateAsync(parsed.Value, rewritten, CancellationToken.None);

        var after = await cache.GetAsync("pipeline", 1, CancellationToken.None);

        after.Value.Nodes.Should().ContainKey("audit", "the write path must drop the key it rewrote, or the cache keeps serving the graph that version no longer has");
        inner.GetCallCount.Should().Be(2, "the resolve after the rewrite has to reach the store again");
    }

    [Fact]
    public async Task A_successful_removal_drops_its_cached_entry()
    {
        var inner = new InMemoryProcessDefinitionStore().Seed(Yaml("pipeline", 1, "done"));
        var cache = new CachingProcessDefinitionStore(inner);

        (await cache.GetAsync("pipeline", 1, CancellationToken.None)).IsSuccess.Should().BeTrue();
        cache.Count.Should().Be(1);

        (await cache.TryRemoveAsync("pipeline", 1, CancellationToken.None)).IsSuccess.Should().BeTrue();

        cache.Count.Should().Be(0);
        (await cache.GetAsync("pipeline", 1, CancellationToken.None)).IsFailure.Should().BeTrue("the row is gone, so the cache must not keep answering for it");
    }

    /// <summary>
    /// A removal the store refuses — because a run still pins the version — leaves the row in place, so evicting
    /// the cached parse would throw away a hot entry for a version that is still perfectly valid and, worse,
    /// suggest the definition had gone away. Turns red if eviction stops being conditional on success.
    /// </summary>
    [Fact]
    public async Task A_refused_removal_keeps_its_cached_entry()
    {
        var inner = new InMemoryProcessDefinitionStore().Seed(Yaml("pipeline", 1, "done"));
        var cache = new CachingProcessDefinitionStore(inner);

        (await cache.GetAsync("pipeline", 1, CancellationToken.None)).IsSuccess.Should().BeTrue();
        inner.RefuseRemoval = true;

        (await cache.TryRemoveAsync("pipeline", 1, CancellationToken.None)).IsFailure.Should().BeTrue();

        cache.Count.Should().Be(1, "the version is still stored and still pinned, so its cached parse is still correct");
        (await cache.GetAsync("pipeline", 1, CancellationToken.None)).IsSuccess.Should().BeTrue();
        inner.GetCallCount.Should().Be(1, "the entry was never evicted, so no second read should have happened");
    }

    /// <summary>
    /// A resolve that fails must not be remembered: a dispatch can legitimately race ahead of the sync that
    /// stores its definition, and caching that miss would leave the process unrunnable for the host's whole
    /// lifetime rather than for one run. Turns red if the failure path starts populating the map.
    /// </summary>
    [Fact]
    public async Task A_failed_resolve_is_not_cached()
    {
        var inner = new InMemoryProcessDefinitionStore();
        var cache = new CachingProcessDefinitionStore(inner);

        (await cache.GetAsync("pipeline", 1, CancellationToken.None)).IsFailure.Should().BeTrue();
        cache.Count.Should().Be(0);

        // The definition arrives late — exactly the race a negative cache would make permanent.
        inner.Seed(Yaml("pipeline", 1, "done"));

        (await cache.GetAsync("pipeline", 1, CancellationToken.None)).IsSuccess.Should().BeTrue("a definition that lands after a failed resolve must become resolvable");
    }

    /// <summary>
    /// The hard bound on growth. Turns red if <see cref="CachingProcessDefinitionStore.TrimToCapacity"/> stops
    /// evicting — the map would then grow with every distinct version a long-lived host ever resolves.
    /// </summary>
    [Fact]
    public async Task The_cache_never_holds_more_than_its_capacity()
    {
        var inner = new InMemoryProcessDefinitionStore();
        for (var version = 1; version <= 5; version++)
        {
            inner.Seed(Yaml("pipeline", version, "done"));
        }

        var cache = new CachingProcessDefinitionStore(inner, capacity: 2);
        for (var version = 1; version <= 5; version++)
        {
            (await cache.GetAsync("pipeline", version, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        cache.Count.Should().Be(2, "five distinct versions were resolved through a cache capped at two");
    }

    /// <summary>
    /// Eviction is oldest-inserted-first, so the version resolved first is the one dropped. Asserted through the
    /// inner store's call count rather than <c>Count</c>, because <c>Count</c> alone cannot say <em>which</em>
    /// entries survived — a policy that evicted the newest would keep the count at two and still be wrong.
    /// </summary>
    [Fact]
    public async Task Eviction_drops_the_oldest_inserted_entry_first()
    {
        var inner = new InMemoryProcessDefinitionStore()
            .Seed(Yaml("pipeline", 1, "done"))
            .Seed(Yaml("pipeline", 2, "done"))
            .Seed(Yaml("pipeline", 3, "done"));
        var cache = new CachingProcessDefinitionStore(inner, capacity: 2);

        await cache.GetAsync("pipeline", 1, CancellationToken.None);
        await cache.GetAsync("pipeline", 2, CancellationToken.None);
        await cache.GetAsync("pipeline", 3, CancellationToken.None);
        inner.GetCallCount.Should().Be(3);

        // Version 2 and 3 should still be cached; version 1 was the oldest and is gone.
        await cache.GetAsync("pipeline", 2, CancellationToken.None);
        await cache.GetAsync("pipeline", 3, CancellationToken.None);
        inner.GetCallCount.Should().Be(3, "versions 2 and 3 must still be cached");

        await cache.GetAsync("pipeline", 1, CancellationToken.None);
        inner.GetCallCount.Should().Be(4, "version 1 was the oldest entry and must have been the one evicted");
    }

    /// <summary>
    /// <see cref="CachingProcessDefinitionStore.GetActiveVersionAsync"/> is deliberately a pass-through: which
    /// version is active is the one fact a sync changes, and a stale answer would start new runs on a superseded
    /// version. Turns red if it ever starts caching.
    /// </summary>
    [Fact]
    public async Task The_active_version_is_never_cached()
    {
        var inner = new InMemoryProcessDefinitionStore().Seed(Yaml("pipeline", 1, "done"));
        var cache = new CachingProcessDefinitionStore(inner);

        (await cache.GetActiveVersionAsync("pipeline", CancellationToken.None)).Should().Be(1);

        inner.Seed(Yaml("pipeline", 2, "done"));

        (await cache.GetActiveVersionAsync("pipeline", CancellationToken.None)).Should().Be(2, "a newly activated version must be visible immediately");
    }
}
