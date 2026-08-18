using Thalos.Skills;

namespace Thalos.Tests.Skills;

public sealed class SkillNameTests
{
    [Theory]
    [InlineData("release")]
    [InlineData("dotnet-migrations")]
    [InlineData("a")]
    [InlineData("a0")]
    [InlineData("a_b-c9")]
    public void Valid_names_parse_and_round_trip(string value)
    {
        SkillName.IsValid(value).Should().BeTrue();
        SkillName.TryParse(value, out var name).Should().BeTrue();
        name.Value.Should().Be(value);
        name.ToString().Should().Be(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0release")]
    [InlineData("-release")]
    [InlineData("_release")]
    [InlineData("re lease")]
    [InlineData("re.lease")]
    [InlineData("release/notes")]
    public void Invalid_names_are_rejected(string? value)
    {
        SkillName.IsValid(value).Should().BeFalse();
        SkillName.TryParse(value, out var name).Should().BeFalse();
        name.Value.Should().BeEmpty();
    }

    [Fact]
    public void A_name_longer_than_64_characters_is_rejected_and_exactly_64_is_accepted()
    {
        var sixtyFour = "a" + new string('b', 63);
        SkillName.IsValid(sixtyFour).Should().BeTrue();
        SkillName.IsValid(sixtyFour + "b").Should().BeFalse();
    }

    [Fact]
    public void TryParse_trims_and_lower_cases_but_Parse_throws_on_rubbish()
    {
        SkillName.TryParse("  Dotnet-Migrations \t", out var name).Should().BeTrue();
        name.Value.Should().Be("dotnet-migrations");
        SkillName.Parse("release").Should().Be(SkillName.Parse("RELEASE"));
        var act = () => SkillName.Parse("not a name");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Default_is_empty_orders_ordinally_and_equals_by_value()
    {
        default(SkillName).Value.Should().BeEmpty();
        default(SkillName).ToString().Should().BeEmpty();
        var a = SkillName.Parse("alpha");
        var b = SkillName.Parse("beta");
        a.CompareTo(b).Should().BeNegative();
        a.Should().Be(SkillName.Parse("alpha"));
        a.Should().NotBe(b);
        new[] { b, a }.Order().Select(n => n.Value).Should().Equal(["alpha", "beta"]);
    }

    [Fact]
    public void Tags_are_normalised_and_deduplicated_in_order()
    {
        SkillRules.NormalizeTags([" DotNet ", "ef", "dotnet", "", "  ", "EF"]).Should().Equal(["dotnet", "ef"]);
        SkillRules.NormalizeTags(null).Should().BeEmpty();
    }
}
