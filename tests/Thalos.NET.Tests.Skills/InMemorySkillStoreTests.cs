using Microsoft.Extensions.Time.Testing;
using Thalos.Skills;

namespace Thalos.Tests.Skills;

public sealed class InMemorySkillStoreTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Upsert_replaces_by_name_and_normalises_tags()
    {
        var store = new InMemorySkillStore(Clock());
        var first = await store.UpsertAsync(SkillModelTests.Doc(tags: [" Release ", "release"]), CancellationToken.None);
        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.ToString() : "");
        first.Value.Tags.Should().Equal(["release"]);

        await store.UpsertAsync(SkillModelTests.Doc(description: "Updated."), CancellationToken.None);
        var got = await store.GetAsync(SkillName.Parse("release"), CancellationToken.None);
        got.Value.Description.Should().Be("Updated.");
        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Should().ContainSingle();
    }

    [Fact]
    public async Task Get_unknown_returns_SkillNotFound()
    {
        var store = new InMemorySkillStore(Clock());
        var got = await store.GetAsync(SkillName.Parse("nope"), CancellationToken.None);
        got.IsFailure.Should().BeTrue();
        got.Error.Code.Should().Be(AgentErrorCode.SkillNotFound);
        got.Error.Message.Should().Contain("nope");
    }

    [Fact]
    public async Task DeactivateMissing_deactivates_the_unseen_and_stamps_the_clock()
    {
        var clock = Clock();
        var store = new InMemorySkillStore(clock);
        await store.UpsertAsync(SkillModelTests.Doc("release"), CancellationToken.None);
        await store.UpsertAsync(SkillModelTests.Doc("gone"), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(5));
        var result = await store.DeactivateMissingAsync([SkillName.Parse("release")], CancellationToken.None);
        result.IsSuccess.Should().BeTrue();

        (await store.ListAsync(new SkillQuery(), CancellationToken.None)).Value.Select(s => s.Name.Value).Should().Equal(["release"]);
        var all = (await store.ListAsync(new SkillQuery { IncludeInactive = true }, CancellationToken.None)).Value;
        all.Select(s => s.Name.Value).Should().Equal(["gone", "release"], "list is ordered by name");
        all[0].IsActive.Should().BeFalse();
        all[0].UpdatedAt.Should().Be(clock.GetUtcNow());
        all[1].UpdatedAt.Should().NotBe(clock.GetUtcNow(), "an untouched skill keeps its timestamp");
    }
}
