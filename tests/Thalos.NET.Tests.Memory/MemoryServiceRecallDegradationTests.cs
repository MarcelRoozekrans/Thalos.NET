using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Memory;
using Thalos.Runtime;

namespace Thalos.Tests.Memory;

/// <summary>
/// Task A2: recall must degrade to the store's most recent in-scope rows rather than go quiet when the semantic index is
/// unavailable or empty-handed (<see cref="MemoryRecallTier"/>) — the failure mode this guards against is a healthy-looking
/// empty recall being indistinguishable from "there is genuinely nothing to know" (see the phase 2.3 task-A2 brief).
/// </summary>
public sealed class MemoryServiceRecallDegradationTests
{
    [Fact]
    public async Task Index_unavailable_with_rows_in_scope_degrades_to_Recency_with_real_records()
    {
        var f = new MemoryServiceFixture(UnavailableMemoryIndex.Instance);
        var svc = f.Build();
        var older = (await svc.RememberAsync(MemoryServiceFixture.Remember("alpha one"), default)).Value;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var newer = (await svc.RememberAsync(MemoryServiceFixture.Remember("alpha two"), default)).Value;

        var r = await svc.RecallAsync("alpha", new MemoryScope("alice", null), new RecallOptions(), default);

        r.IsSuccess.Should().BeTrue("an index outage degrades recall, it does not fail it");
        r.Value.Tier.Should().Be(MemoryRecallTier.Recency);
        r.Value.Memories.Select(m => m.Record.Id).Should().Equal([newer.Id, older.Id], "recency orders by UpdatedAt descending");
    }

    [Fact]
    public async Task Warm_index_with_a_real_hit_still_answers_Semantic()
    {
        // without this, a fallback that runs unconditionally could silently replace semantic recall everywhere
        // and no test would notice — this is the guard against that regression.
        var f = new MemoryServiceFixture(); // real cosine index over the bag-of-words generator
        var svc = f.Build();
        await svc.RememberAsync(MemoryServiceFixture.Remember("deploy the api with blue green releases"), default);

        var r = await svc.RecallAsync("deploy the api with blue green releases", new MemoryScope("alice", null), new RecallOptions { MinScore = 0.1 }, default);

        r.IsSuccess.Should().BeTrue();
        r.Value.Tier.Should().Be(MemoryRecallTier.Semantic);
        r.Value.Memories.Should().ContainSingle();
    }

    [Fact]
    public async Task Healthy_index_returning_zero_hits_degrades_to_Recency_not_None()
    {
        // the exact case the consuming project hit: the index is up and answers, but nothing in it scores as a match
        // (e.g. rows that were never embedded) — this must still surface the in-scope rows, not report "nothing to know".
        var f = new MemoryServiceFixture(); // real index, healthy probe
        var svc = f.Build();
        var stored = (await svc.RememberAsync(MemoryServiceFixture.Remember("completely unrelated content"), default)).Value;

        // MinScore 0.99 forces the healthy index's own search to come back with zero hits regardless of hash collisions
        var r = await svc.RecallAsync("a query sharing no meaning with the stored text", new MemoryScope("alice", null), new RecallOptions { MinScore = 0.99 }, default);

        r.IsSuccess.Should().BeTrue();
        r.Value.Tier.Should().Be(MemoryRecallTier.Recency);
        r.Value.Memories.Should().ContainSingle(m => m.Record.Id == stored.Id);
    }

    [Fact]
    public async Task No_rows_in_scope_at_all_is_None_and_empty_not_an_error()
    {
        var f = new MemoryServiceFixture(UnavailableMemoryIndex.Instance);
        var svc = f.Build();

        var r = await svc.RecallAsync("anything", new MemoryScope("alice", null), new RecallOptions(), default);

        r.IsSuccess.Should().BeTrue();
        r.Value.Tier.Should().Be(MemoryRecallTier.None);
        r.Value.Memories.Should().BeEmpty();
    }

    [Fact]
    public async Task Recency_fallback_respects_MemoryScope_excludes_other_owner_and_other_agent_pin()
    {
        // real seeded rows under a different owner AND a different agent pin: a scope test over an empty store would
        // pass whatever the filter said, so both must genuinely exist for this assertion to mean anything.
        var f = new MemoryServiceFixture(UnavailableMemoryIndex.Instance);
        var svc = f.Build();
        var callersAgent = AgentId.New();
        var otherAgent = AgentId.New();
        var ownShared = (await svc.RememberAsync(MemoryServiceFixture.Remember("alice shared memory"), default)).Value;
        var ownPinned = (await svc.RememberAsync(MemoryServiceFixture.Remember("alice pinned to the caller's agent", agent: callersAgent), default)).Value;
        await svc.RememberAsync(MemoryServiceFixture.Remember("alice pinned to a different agent", agent: otherAgent), default);
        await svc.RememberAsync(MemoryServiceFixture.Remember("bobs memory entirely", owner: "bob"), default);

        var r = await svc.RecallAsync("memory", new MemoryScope("alice", callersAgent), new RecallOptions(), default);

        r.Value.Tier.Should().Be(MemoryRecallTier.Recency);
        r.Value.Memories.Select(m => m.Record.Id).Should().BeEquivalentTo([ownShared.Id, ownPinned.Id],
            "neither bob's memory nor the one pinned to a different agent may leak through the degraded path");
    }

    [Fact]
    public async Task Recency_fallback_does_not_lose_in_scope_rows_behind_another_agents_page_boundary()
    {
        // Reproduction from review: owner alice, 5 rows pinned to myAgent, then MORE than MaxPageSize newer rows pinned to
        // otherAgent. A query that lists the owner's records without an agent filter, ordered by UpdatedAt desc, would put
        // every otherAgent row ahead of the 5 in-scope ones on page 1 and never see them at all. One exact-filtered query per
        // MemoryScope.Partitions() entry must not have this failure mode: myAgent's partition is queried on its own.
        var f = new MemoryServiceFixture(UnavailableMemoryIndex.Instance);
        var svc = f.Build();
        var myAgent = AgentId.New();
        var otherAgent = AgentId.New();
        var mine = new List<MemoryId>();
        for (var i = 0; i < 5; i++)
        {
            mine.Add((await svc.RememberAsync(MemoryServiceFixture.Remember($"mine {i}", agent: myAgent), default)).Value.Id);
        }

        for (var i = 0; i < MemoryQuery.MaxPageSize + 5; i++)
        {
            await svc.RememberAsync(MemoryServiceFixture.Remember($"other agent noise {i}", agent: otherAgent), default);
        }

        var r = await svc.RecallAsync("mine", new MemoryScope("alice", myAgent), new RecallOptions { TopK = 20 }, default);

        r.IsSuccess.Should().BeTrue();
        r.Value.Tier.Should().Be(MemoryRecallTier.Recency, "the store genuinely holds in-scope rows; this must never present as None");
        r.Value.Memories.Select(m => m.Record.Id).Should().BeEquivalentTo(mine, "none of the caller's own pinned rows may be dropped behind another agent's page of noise");
    }

    [Fact]
    public async Task Recency_fallback_store_failure_is_returned_not_swallowed_into_an_empty_success()
    {
        var f = new MemoryServiceFixture(UnavailableMemoryIndex.Instance);
        var store = new HookedStore(f.Store) { OnList = _ => AgentError.MemoryStoreFailed("store down", "Test") };
        var svc = f.Build(store);

        var r = await svc.RecallAsync("anything", new MemoryScope("alice", null), new RecallOptions(), default);

        r.IsFailure.Should().BeTrue("a genuine store failure during the recency fallback must surface, not degrade to an empty success");
        r.Error.Code.Should().Be(AgentErrorCode.MemoryStoreFailed);
    }

    [Fact]
    public async Task Tool_output_carries_the_degraded_note_only_when_the_tier_is_not_Semantic()
    {
        var (warmFixture, warmSource) = MemoryToolsTests.Build();
        await warmFixture.Build().RememberAsync(MemoryServiceFixture.Remember("deploy with blue green"), default);
        var warmRecall = await MemoryToolsTests.Tool(warmSource, "recall");
        using (TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice")))
        {
            var warm = (await warmRecall.InvokeAsync(MemoryToolsTests.Args(("query", "deploy with blue green"))))!.ToString()!;
            warm.Should().NotContain(MemoryRecallBlock.DegradedRecallNote);
        }

        var (degradedFixture, degradedSource) = MemoryToolsTests.Build(index: UnavailableMemoryIndex.Instance);
        await degradedFixture.Build().RememberAsync(MemoryServiceFixture.Remember("deploy with blue green"), default);
        var degradedRecall = await MemoryToolsTests.Tool(degradedSource, "recall");
        using (TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice")))
        {
            var degraded = (await degradedRecall.InvokeAsync(MemoryToolsTests.Args(("query", "deploy with blue green"))))!.ToString()!;
            degraded.Should().Contain(MemoryRecallBlock.DegradedRecallNote).And.Contain("deploy with blue green");
        }
    }
}
