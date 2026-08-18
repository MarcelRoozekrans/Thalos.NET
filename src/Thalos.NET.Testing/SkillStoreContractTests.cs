using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Thalos.Skills;
using Xunit;

namespace Thalos.Testing;

/// <summary>
/// Behavioural contract every <see cref="ISkillStore"/> must satisfy — the suite Thalos runs against <c>InMemorySkillStore</c>
/// and Daedalus runs against its Postgres store. Derive, implement <see cref="CreateStoreAsync"/> (a fresh, empty store reading
/// time from the given clock), let xUnit discover the inherited facts.
/// </summary>
/// <remarks>
/// What the suite assumes beyond the interface docs: <see cref="ISkillStore.ListAsync"/> orders by name <em>ordinally</em>
/// ascending; <c>UpdatedAt</c> round-trips with millisecond precision and <see cref="ISkillStore.DeactivateMissingAsync"/>
/// stamps it from the injected <see cref="TimeProvider"/>, not a database-side <c>now()</c>; an upsert of an existing name
/// replaces every field (including reactivating an inactive skill); empty-but-non-null filter lists mean "no filter"; and a
/// 300-character description, a 65 536-character multi-line non-BMP body and ten 32-character tags all round-trip unchanged.
/// </remarks>
public abstract class SkillStoreContractTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);

    /// <summary>Creates a fresh, empty store whose clock is <paramref name="clock"/> (a <see cref="FakeTimeProvider"/> the suite advances).</summary>
    protected abstract ValueTask<ISkillStore> CreateStoreAsync(TimeProvider clock);

    /// <summary>A fake clock starting at 2026-08-18 12:00 UTC (advance it between operations).</summary>
    protected static FakeTimeProvider NewClock() => new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));

    /// <summary>A valid document timestamped from <paramref name="clock"/>.</summary>
    protected static SkillDocument NewSkill(TimeProvider clock, string name = "release", string? description = null, string? body = null, IReadOnlyList<string>? tags = null, string? hash = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new SkillDocument
        {
            Name = SkillName.Parse(name),
            Description = description ?? "How we cut and publish a release.",
            Body = body ?? "# Releasing\n1. Tag it.\n",
            Tags = tags ?? ["release"],
            SourcePath = name + "/SKILL.md",
            ContentHash = hash ?? new string('a', 64),
            UpdatedAt = clock.GetUtcNow(),
        };
    }

    [Fact]
    public async Task Upsert_then_Get_roundtrips_every_field()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var skill = NewSkill(clock, tags: ["a", "b"]);

        var stored = await store.UpsertAsync(skill, CancellationToken.None);
        stored.IsSuccess.Should().BeTrue(stored.IsFailure ? stored.Error.ToString() : "");

        var got = await store.GetAsync(skill.Name, CancellationToken.None);
        got.IsSuccess.Should().BeTrue();
        got.Value.Should().BeEquivalentTo(skill, o => o.Excluding(s => s.UpdatedAt));
        got.Value.UpdatedAt.Should().BeCloseTo(skill.UpdatedAt, Tolerance);
        got.Value.Tags.Should().Equal(["a", "b"]);
        got.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Upsert_replaces_every_field_of_an_existing_name()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.UpsertAsync(NewSkill(clock), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        var replacement = NewSkill(clock, description: "New words.", body: "# New\n", tags: ["x"], hash: new string('b', 64));
        await store.UpsertAsync(replacement, CancellationToken.None);

        var got = (await store.GetAsync(replacement.Name, CancellationToken.None)).Value;
        got.Description.Should().Be("New words.");
        got.Body.Should().Be("# New\n");
        got.Tags.Should().Equal(["x"]);
        got.ContentHash.Should().Be(replacement.ContentHash);
        got.UpdatedAt.Should().BeCloseTo(replacement.UpdatedAt, Tolerance);
        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().ContainSingle();
    }

    [Fact]
    public async Task Get_unknown_returns_SkillNotFound()
    {
        var store = await CreateStoreAsync(NewClock());
        var got = await store.GetAsync(SkillName.Parse("nothing-here"), CancellationToken.None);
        got.IsFailure.Should().BeTrue();
        got.Error.Code.Should().Be(AgentErrorCode.SkillNotFound);
    }

    [Fact]
    public async Task Upsert_normalises_tags()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var stored = await store.UpsertAsync(NewSkill(clock, tags: ["Foo", " foo ", "BAR"]), CancellationToken.None);
        stored.Value.Tags.Should().Equal(["foo", "bar"]);
        (await store.GetAsync(stored.Value.Name, CancellationToken.None)).Value.Tags.Should().Equal(["foo", "bar"]);
    }

    [Fact]
    public async Task List_orders_by_name_and_hides_inactive_skills()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.UpsertAsync(NewSkill(clock, "zeta"), CancellationToken.None);
        await store.UpsertAsync(NewSkill(clock, "alpha"), CancellationToken.None);
        await store.UpsertAsync(NewSkill(clock, "mid"), CancellationToken.None);
        await store.DeactivateMissingAsync([SkillName.Parse("alpha"), SkillName.Parse("zeta")], CancellationToken.None);

        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Select(s => s.Name.Value).Should().Equal(["alpha", "zeta"]);
        (await store.ListAsync(new SkillQuery { IncludeInactive = true }, CancellationToken.None)).Value.Select(s => s.Name.Value).Should().Equal(["alpha", "mid", "zeta"]);
    }

    [Fact]
    public async Task List_filters_by_name_and_by_tag_and_empty_filters_mean_no_filter()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.UpsertAsync(NewSkill(clock, "release", tags: ["ops"]), CancellationToken.None);
        await store.UpsertAsync(NewSkill(clock, "migrations", tags: ["dotnet", "ef"]), CancellationToken.None);

        (await store.ListAsync(new SkillQuery { Names = [SkillName.Parse("release")] }, CancellationToken.None)).Value.Should().ContainSingle(s => s.Name.Value == "release");
        (await store.ListAsync(new SkillQuery { Tags = ["EF "] }, CancellationToken.None)).Value.Should().ContainSingle(s => s.Name.Value == "migrations");
        (await store.ListAsync(new SkillQuery { Tags = ["dotnet", "ops"] }, CancellationToken.None)).Value.Should().BeEmpty("every listed tag must be present");
        (await store.ListAsync(new SkillQuery { Names = [], Tags = [] }, CancellationToken.None)).Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeactivateMissing_only_touches_active_unseen_skills_and_stamps_the_clock()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.UpsertAsync(NewSkill(clock, "keep"), CancellationToken.None);
        await store.UpsertAsync(NewSkill(clock, "drop"), CancellationToken.None);
        var created = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromMinutes(3));
        await store.DeactivateMissingAsync([SkillName.Parse("keep"), SkillName.Parse("keep")], CancellationToken.None);
        var afterFirst = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromMinutes(3));
        await store.DeactivateMissingAsync([SkillName.Parse("keep")], CancellationToken.None);

        var all = (await store.ListAsync(new SkillQuery { IncludeInactive = true }, CancellationToken.None)).Value;
        var drop = all.Single(s => s.Name == SkillName.Parse("drop"));
        var keep = all.Single(s => s.Name == SkillName.Parse("keep"));
        drop.IsActive.Should().BeFalse();
        drop.UpdatedAt.Should().BeCloseTo(afterFirst, Tolerance, "an already-inactive skill is not stamped again");
        keep.IsActive.Should().BeTrue();
        keep.UpdatedAt.Should().BeCloseTo(created, Tolerance);
    }

    [Fact]
    public async Task DeactivateMissing_with_an_empty_list_deactivates_everything()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.UpsertAsync(NewSkill(clock, "one"), CancellationToken.None);
        await store.UpsertAsync(NewSkill(clock, "two"), CancellationToken.None);

        (await store.DeactivateMissingAsync([], CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().BeEmpty();
        (await store.ListAsync(new SkillQuery { IncludeInactive = true }, CancellationToken.None)).Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_upsert_reactivates_a_deactivated_skill()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.UpsertAsync(NewSkill(clock, "back"), CancellationToken.None);
        await store.DeactivateMissingAsync([], CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        await store.UpsertAsync(NewSkill(clock, "back"), CancellationToken.None);

        (await store.GetAsync(SkillName.Parse("back"), CancellationToken.None)).Value.IsActive.Should().BeTrue();
        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().ContainSingle();
    }

    [Fact]
    public async Task Boundary_lengths_roundtrip()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var body = string.Concat(Enumerable.Repeat("step 🚀\n", 8192))[..SkillDocument.MaxBodyChars];
        var skill = NewSkill(
            clock,
            description: new string('d', SkillDocument.MaxDescriptionLength),
            body: body,
            tags: Enumerable.Range(0, SkillDocument.MaxTags).Select(i => "t" + new string('x', SkillDocument.MaxTagLength - 2) + i.ToString(CultureInfo.InvariantCulture)).ToArray());

        (await store.UpsertAsync(skill, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var got = (await store.GetAsync(skill.Name, CancellationToken.None)).Value;
        got.Description.Should().HaveLength(SkillDocument.MaxDescriptionLength);
        got.Body.Should().Be(body);
        got.Tags.Should().HaveCount(SkillDocument.MaxTags);
        Skills.SkillRules.Validate(got).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_upserts_of_different_skills_all_land()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
            (await store.UpsertAsync(NewSkill(clock, "skill-" + i.ToString(CultureInfo.InvariantCulture)), CancellationToken.None).ConfigureAwait(false)).IsSuccess.Should().BeTrue()));

        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().HaveCount(20);
    }
}
