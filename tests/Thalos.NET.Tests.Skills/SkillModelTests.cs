using Thalos.Skills;

namespace Thalos.Tests.Skills;

public sealed class SkillModelTests
{
    internal static SkillDocument Doc(string name = "release", string? description = null, string? body = null, IReadOnlyList<string>? tags = null) => new()
    {
        Name = SkillName.Parse(name),
        Description = description ?? "How we cut and publish a release.",
        Body = body ?? "# Releasing\n1. Tag it.\n",
        Tags = tags ?? ["release"],
        SourcePath = name + "/SKILL.md",
        ContentHash = new string('a', 64),
        UpdatedAt = new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void A_well_formed_document_validates_and_is_active_by_default()
    {
        var doc = Doc();
        doc.IsActive.Should().BeTrue();
        SkillRules.Validate(doc).Should().BeNull();
    }

    [Theory]
    [InlineData("Description")]
    [InlineData("Body")]
    [InlineData("SourcePath")]
    [InlineData("ContentHash")]
    public void A_blank_required_string_is_a_validation_failure_naming_the_property(string property)
    {
        var doc = property switch
        {
            "Description" => Doc() with { Description = "  " },
            "Body" => Doc() with { Body = "\n \n" },
            "SourcePath" => Doc() with { SourcePath = "" },
            _ => Doc() with { ContentHash = "" },
        };

        var error = SkillRules.Validate(doc);
        error.Should().NotBeNull();
        error!.Value.Code.Should().Be(AgentErrorCode.SkillValidationFailed);
        error.Value.Message.Should().Contain(property);
    }

    [Fact]
    public void Limits_are_enforced_at_the_boundary_and_one_over()
    {
        SkillRules.Validate(Doc(description: new string('d', SkillDocument.MaxDescriptionLength))).Should().BeNull();
        SkillRules.Validate(Doc(description: new string('d', SkillDocument.MaxDescriptionLength + 1))).Should().NotBeNull();
        SkillRules.Validate(Doc(body: new string('b', SkillDocument.MaxBodyChars))).Should().BeNull();
        SkillRules.Validate(Doc(body: new string('b', SkillDocument.MaxBodyChars + 1))).Should().NotBeNull();
        SkillRules.Validate(Doc(tags: Enumerable.Range(0, SkillDocument.MaxTags).Select(i => $"t{i}").ToArray())).Should().BeNull();
        SkillRules.Validate(Doc(tags: Enumerable.Range(0, SkillDocument.MaxTags + 1).Select(i => $"t{i}").ToArray())).Should().NotBeNull();
        SkillRules.Validate(Doc(tags: [new string('t', SkillDocument.MaxTagLength + 1)])).Should().NotBeNull();
    }

    [Fact]
    public void A_default_name_is_a_validation_failure()
    {
        var error = SkillRules.Validate(Doc() with { Name = default });
        error.Should().NotBeNull();
        error!.Value.Message.Should().Contain("Name");
    }

    [Fact]
    public void Query_matches_active_skills_by_name_and_tag_and_hides_inactive_ones()
    {
        var release = Doc();
        var migrations = Doc("dotnet-migrations", tags: ["dotnet", "ef"]);
        var retired = Doc("retired") with { IsActive = false };

        new SkillQuery().Matches(release).Should().BeTrue();
        new SkillQuery().Matches(retired).Should().BeFalse("inactive skills are hidden unless asked for");
        new SkillQuery { IncludeInactive = true }.Matches(retired).Should().BeTrue();

        new SkillQuery { Names = [SkillName.Parse("release")] }.Matches(release).Should().BeTrue();
        new SkillQuery { Names = [SkillName.Parse("release")] }.Matches(migrations).Should().BeFalse();
        new SkillQuery { Names = [] }.Matches(migrations).Should().BeTrue("an empty filter list means no filter");

        new SkillQuery { Tags = [" DotNet "] }.Matches(migrations).Should().BeTrue("query tags are normalised like stored tags");
        new SkillQuery { Tags = ["dotnet", "ef"] }.Matches(migrations).Should().BeTrue("every listed tag must be present");
        new SkillQuery { Tags = ["dotnet", "nope"] }.Matches(migrations).Should().BeFalse();
    }

    [Fact]
    public void Options_carry_the_documented_defaults()
    {
        var o = new SkillOptions();
        SkillOptions.SectionName.Should().Be("Thalos:Skills");
        o.Enabled.Should().BeTrue();
        o.ExposeTools.Should().BeTrue();
        o.SyncOnStartup.Should().BeTrue();
        o.Roots.Should().BeEmpty();
        o.Catalogue.MaxChars.Should().Be(2000);
        o.Search.TopK.Should().Be(5);
        o.Search.MinScore.Should().Be(0.3);
    }
}
