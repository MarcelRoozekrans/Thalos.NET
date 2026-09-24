using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Thalos.Skills.Charters;
using Xunit;

namespace Thalos.Testing;

/// <summary>
/// Behavioural contract every <see cref="IRoleCharterStore"/> must satisfy — the suite Thalos runs against
/// <c>InMemoryRoleCharterStore</c> and Daedalus runs against its Postgres store. Derive, implement
/// <see cref="CreateStoreAsync"/> (a fresh, empty store reading time from the given clock), let xUnit discover the
/// inherited facts.
/// </summary>
/// <remarks>
/// What the suite assumes beyond the interface docs: every upsert appends a new version rather than replacing one —
/// <see cref="IRoleCharterStore.ListVersionsAsync"/> must return every content hash a role was ever upserted with —
/// and only the role's current version (its most recent upsert) is <see cref="RoleCharter.IsActive"/>, provided the
/// role itself has not been deactivated. <see cref="IRoleCharterStore.DeactivateMissingAsync"/> never deletes a
/// version: it only flips a role's current version's <see cref="RoleCharter.IsActive"/> to false.
/// </remarks>
public abstract class RoleCharterStoreContractTests
{
    /// <summary>Creates a fresh, empty store whose clock is <paramref name="clock"/> (a <see cref="FakeTimeProvider"/> the suite advances).</summary>
    protected abstract ValueTask<IRoleCharterStore> CreateStoreAsync(TimeProvider clock);

    /// <summary>A fake clock starting at 2026-09-23 12:00 UTC (advance it between operations).</summary>
    protected static FakeTimeProvider NewClock() => new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

    /// <summary>A valid charter for <paramref name="role"/> with content hash <paramref name="hash"/>.</summary>
    protected static RoleCharter NewCharter(string role, string hash) => new()
    {
        Role = role,
        Description = "d",
        Instructions = $"Instructions {hash}.",
        SourcePath = $"roles/{role}.md",
        ContentHash = hash,
        UpdatedAt = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Every_version_is_listed_and_only_the_latest_is_active()
    {
        var store = await CreateStoreAsync(NewClock());
        await store.UpsertAsync(NewCharter("reviewer", "hash-1"), CancellationToken.None);
        await store.UpsertAsync(NewCharter("reviewer", "hash-2"), CancellationToken.None);

        var all = (await store.ListVersionsAsync(CancellationToken.None)).Value;
        all.Select(c => c.ContentHash).Should().BeEquivalentTo(["hash-1", "hash-2"]);
        all.Single(c => c.IsActive).ContentHash.Should().Be("hash-2");
    }

    [Fact]
    public async Task DeactivateMissing_deactivates_the_role_but_keeps_its_versions()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.UpsertAsync(NewCharter("reviewer", "hash-1"), CancellationToken.None);
        await store.UpsertAsync(NewCharter("implementer", "hash-i"), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(5));
        await store.DeactivateMissingAsync(["implementer"], CancellationToken.None);

        var all = (await store.ListVersionsAsync(CancellationToken.None)).Value;
        var reviewer = all.Single(c => string.Equals(c.Role, "reviewer", StringComparison.Ordinal));
        reviewer.IsActive.Should().BeFalse();
        reviewer.UpdatedAt.Should().Be(clock.GetUtcNow(), "deactivation stamps the version from the store's clock, not a database-side now()");
        all.Should().Contain(c => string.Equals(c.Role, "implementer", StringComparison.Ordinal) && c.IsActive);
    }

    [Fact]
    public async Task Reupserting_a_deactivated_role_reactivates_it()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.UpsertAsync(NewCharter("reviewer", "hash-1"), CancellationToken.None);
        await store.DeactivateMissingAsync([], CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        await store.UpsertAsync(NewCharter("reviewer", "hash-2"), CancellationToken.None);

        var all = (await store.ListVersionsAsync(CancellationToken.None)).Value;
        all.Should().Contain(
            c => string.Equals(c.Role, "reviewer", StringComparison.Ordinal) && string.Equals(c.ContentHash, "hash-1", StringComparison.Ordinal) && !c.IsActive,
            "the deactivated version is kept, not deleted");
        all.Single(c => c.IsActive).ContentHash.Should().Be("hash-2");
    }

    [Fact]
    public async Task Reupserting_an_older_hash_makes_it_current()
    {
        var store = await CreateStoreAsync(NewClock());
        await store.UpsertAsync(NewCharter("reviewer", "hash-1"), CancellationToken.None);
        await store.UpsertAsync(NewCharter("reviewer", "hash-2"), CancellationToken.None);

        await store.UpsertAsync(NewCharter("reviewer", "hash-1"), CancellationToken.None);

        var all = (await store.ListVersionsAsync(CancellationToken.None)).Value;
        all.Should().HaveCount(2, "re-upserting an existing hash must not create a third version");
        all.Single(c => c.IsActive).ContentHash.Should().Be("hash-1", "the most recently upserted hash is current, even if it was seen before");
    }
}
