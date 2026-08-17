using Thalos.Memory;

namespace Thalos.Tests.Memory;

public sealed class MemoryServiceDedupeTests
{
    [Fact]
    public async Task Near_duplicate_refreshes_the_existing_record_instead_of_inserting()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        var first = (await svc.RememberAsync(MemoryServiceFixture.Remember("The user prefers xUnit over NUnit.", importance: 0.4), default)).Value;
        f.Clock.Advance(TimeSpan.FromMinutes(5));

        var again = await svc.RememberAsync(MemoryServiceFixture.Remember("the user prefers xunit over nunit", importance: 0.7), default);

        again.IsSuccess.Should().BeTrue();
        again.Value.Id.Should().Be(first.Id);
        again.Value.Importance.Should().Be(0.7, "max of both");
        again.Value.UpdatedAt.Should().Be(f.Clock.GetUtcNow());
        (await f.Store.ListAsync(new MemoryQuery { OwnerIds = ["alice"] }, default)).Value.TotalCount.Should().Be(1);
        f.HubEvents.OfType<MemoryStoredEvent>().Last().Deduped.Should().BeTrue();
    }

    [Fact]
    public async Task Dedupe_is_per_owner_and_never_via_the_shared_owner()
    {
        var f = new MemoryServiceFixture();
        f.Options.SharedOwnerId = "daedalus";
        var svc = f.Build();
        await svc.RememberAsync(MemoryServiceFixture.Remember("Rotate the API key monthly.", owner: "daedalus"), default);
        await svc.RememberAsync(MemoryServiceFixture.Remember("Rotate the API key monthly.", owner: "bob"), default);

        var alice = await svc.RememberAsync(MemoryServiceFixture.Remember("Rotate the API key monthly.", owner: "alice"), default);

        (await f.Store.ListAsync(new MemoryQuery(), default)).Value.TotalCount.Should().Be(3);
        alice.Value.OwnerId.Should().Be("alice");
    }

    [Fact]
    public async Task Different_text_and_disabled_dedupe_both_insert()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        await svc.RememberAsync(MemoryServiceFixture.Remember("alpha bravo"), default);
        await svc.RememberAsync(MemoryServiceFixture.Remember("charlie delta"), default);
        (await f.Store.ListAsync(new MemoryQuery(), default)).Value.TotalCount.Should().Be(2);

        f.Options.Dedupe.Enabled = false;
        await svc.RememberAsync(MemoryServiceFixture.Remember("alpha bravo"), default);
        (await f.Store.ListAsync(new MemoryQuery(), default)).Value.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task Archived_duplicates_are_ignored_and_index_failure_skips_dedupe()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        var first = (await svc.RememberAsync(MemoryServiceFixture.Remember("echo foxtrot"), default)).Value;
        await f.Store.UpdateAsync(first.Id, new MemoryUpdate { IsArchived = true }, default);
        (await svc.RememberAsync(MemoryServiceFixture.Remember("echo foxtrot"), default)).Value.Id.Should().NotBe(first.Id);

        f.Index = UnavailableMemoryIndex.Instance;
        var svc2 = f.Build();
        var r = await svc2.RememberAsync(MemoryServiceFixture.Remember("echo foxtrot"), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.IndexPending.Should().BeTrue();
    }
}
