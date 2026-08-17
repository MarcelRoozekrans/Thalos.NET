using Thalos.Memory;

namespace Thalos.Tests.Memory;

public sealed class ModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    internal static MemoryRecord Record(string owner = "alice", AgentId? agent = null, string text = "The user prefers xUnit.", MemoryKind? kind = null, params string[] tags) => new()
    {
        Id = MemoryId.New(), OwnerId = owner, AgentId = agent, Kind = kind ?? MemoryKind.Fact, Text = text, Tags = tags, CreatedAt = T0, UpdatedAt = T0,
    };

    [Theory]
    [InlineData("fact", true)] [InlineData("Fact ", true)] [InlineData("my-kind_2", true)]
    [InlineData("", false)] [InlineData("2fast", false)] [InlineData("has space", false)] [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg", false)]
    public void MemoryKind_parses_lowercase_identifiers(string input, bool ok)
    {
        MemoryKind.TryParse(input, out var kind).Should().Be(ok);
        if (ok) kind!.Value.Should().Be(input.Trim().ToLowerInvariant());
    }

    [Fact]
    public void Built_in_kinds_are_lowercase_and_equal_by_value()
    {
        MemoryKind.Fact.Value.Should().Be("fact");
        MemoryKind.Learning.Should().Be(new MemoryKind("learning"));
        MemoryKind.TryParse("preference", out var p).Should().BeTrue();
        p.Should().Be(MemoryKind.Preference);
    }

    [Fact]
    public void Valid_record_passes_rules()
    {
        MemoryRules.Validate(Record(tags: ["testing", "prefs"])).Should().BeNull();
    }

    [Theory]
    [InlineData("")] [InlineData("   ")]
    public void Empty_text_fails(string text) => MemoryRules.Validate(Record(text: text))!.Value.Code.Should().Be(AgentErrorCode.MemoryValidationFailed);

    [Fact]
    public void Limits_are_enforced()
    {
        MemoryRules.Validate(Record(text: new string('x', MemoryRecord.MaxTextLength + 1))).Should().NotBeNull();
        MemoryRules.Validate(Record(text: new string('x', MemoryRecord.MaxTextLength))).Should().BeNull();
        MemoryRules.Validate(Record(tags: Enumerable.Range(0, 11).Select(i => $"t{i}").ToArray())).Should().NotBeNull();
        MemoryRules.Validate(Record(tags: [new string('t', 33)])).Should().NotBeNull();
        MemoryRules.Validate(Record() with { Importance = 1.5 }).Should().NotBeNull();
        MemoryRules.Validate(Record() with { Importance = -0.1 }).Should().NotBeNull();
        MemoryRules.Validate(Record() with { Importance = 1.0 }).Should().BeNull();
        MemoryRules.Validate(Record(kind: new MemoryKind("Bad Kind"))).Should().NotBeNull();
        MemoryRules.Validate(Record(owner: "")).Should().NotBeNull();
    }

    [Fact]
    public void NormalizeTags_trims_dedupes_and_drops_blanks()
    {
        MemoryRules.NormalizeTags([" a ", "b", "a", "", "  "]).Should().Equal("a", "b");
        MemoryRules.NormalizeTags(null).Should().BeEmpty();
    }

    [Fact]
    public void Scope_visibility_matrix()
    {
        var a = AgentId.New(); var b = AgentId.New();
        var scope = new MemoryScope("alice", a, "shared-owner");
        scope.Includes("alice", null).Should().BeTrue("owner-wide memories are visible to every agent of the owner");
        scope.Includes("alice", a).Should().BeTrue("pinned to this agent");
        scope.Includes("alice", b).Should().BeFalse("pinned to another agent");
        scope.Includes("bob", null).Should().BeFalse("another owner");
        scope.Includes("shared-owner", null).Should().BeTrue("shared owner, owner-wide");
        scope.Includes("shared-owner", a).Should().BeFalse("shared owner memories are never agent-pinned");
        new MemoryScope("alice", null, null).Includes("alice", a).Should().BeFalse("no agent in scope → only owner-wide");
        new MemoryScope("alice", a, null).Includes("shared-owner", null).Should().BeFalse("no shared owner configured");
    }

    [Fact]
    public void Scope_partitions_are_the_AND_filters_an_index_needs()
    {
        var a = AgentId.New();
        new MemoryScope("alice", a, "shared").Partitions().Should().Equal(("alice", (AgentId?)a), ("alice", null), ("shared", null));
        new MemoryScope("alice", null, null).Partitions().Should().Equal(("alice", (AgentId?)null));
        new MemoryScope("alice", null, "alice").Partitions().Should().Equal([("alice", (AgentId?)null)], "shared owner equal to the owner is not repeated");
    }

    [Fact]
    public void Query_matches_records()
    {
        var a = AgentId.New();
        var r = Record(agent: a, tags: ["x", "y"]);
        new MemoryQuery { OwnerIds = ["alice"] }.Matches(r).Should().BeTrue();
        new MemoryQuery { OwnerIds = ["bob"] }.Matches(r).Should().BeFalse();
        new MemoryQuery().Matches(r).Should().BeTrue("no owner filter = all owners (store level)");
        new MemoryQuery { AgentId = a }.Matches(r).Should().BeTrue();
        new MemoryQuery { AgentId = AgentId.New() }.Matches(r).Should().BeFalse();
        new MemoryQuery { Kinds = [MemoryKind.Fact, MemoryKind.Note] }.Matches(r).Should().BeTrue();
        new MemoryQuery { Kinds = [MemoryKind.Note] }.Matches(r).Should().BeFalse();
        new MemoryQuery { Tags = ["x", "y"] }.Matches(r).Should().BeTrue("all listed tags present");
        new MemoryQuery { Tags = ["x", "z"] }.Matches(r).Should().BeFalse();
        new MemoryQuery().Matches(r with { IsArchived = true }).Should().BeFalse();
        new MemoryQuery { IncludeArchived = true }.Matches(r with { IsArchived = true }).Should().BeTrue();
        new MemoryQuery { IndexPending = true }.Matches(r).Should().BeFalse();
        new MemoryQuery { IndexPending = true }.Matches(r with { IndexPending = true }).Should().BeTrue();
    }

    [Fact]
    public void Options_defaults_match_the_design()
    {
        var o = new MemoryOptions();
        o.Enabled.Should().BeTrue(); o.ExposeTools.Should().BeTrue(); o.SharedOwnerId.Should().BeNull();
        o.Recall.TopK.Should().Be(5); o.Recall.MinScore.Should().Be(0.6); o.Recall.MaxChars.Should().Be(2000);
        o.Dedupe.Enabled.Should().BeTrue(); o.Dedupe.Threshold.Should().Be(0.95);
        MemoryOptions.SectionName.Should().Be("Thalos:Memory");
        new ReindexOptions().PendingOnly.Should().BeTrue();
        new MemoryUpdate { IndexPending = false }.TouchesContent.Should().BeFalse();
        new MemoryUpdate { Importance = 0.9 }.TouchesContent.Should().BeTrue();
    }
}
