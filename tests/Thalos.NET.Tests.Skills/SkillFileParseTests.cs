using Thalos.Skills;
using ZeroAlloc.Results;

namespace Thalos.Tests.Skills;

public sealed class SkillFileParseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static Result<SkillDocument, AgentError> Parse(string text, string expected = "dotnet-migrations") =>
        SkillFileLoader.Parse("dotnet-migrations/SKILL.md", expected, text, Now);

    private const string ValidSource = """
        ---
        name: dotnet-migrations
        description: How to add and apply an EF Core migration in this repo.
        tags: [dotnet, ef, database]
        ---

        # Adding a migration
        1. dotnet ef migrations add <Name>
        """;

    // The raw literal above carries whatever line endings the checkout has; every test starts from the LF form
    // explicitly so that the CRLF/LF assertions below cannot pass vacuously on a CRLF working tree.
    private static readonly string Valid = ValidSource.ReplaceLineEndings("\n");

    [Fact]
    public void A_valid_file_parses_into_a_document()
    {
        var result = Parse(Valid);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : "");
        var doc = result.Value;
        doc.Name.Value.Should().Be("dotnet-migrations");
        doc.Description.Should().Be("How to add and apply an EF Core migration in this repo.");
        doc.Tags.Should().Equal(["dotnet", "ef", "database"]);
        doc.SourcePath.Should().Be("dotnet-migrations/SKILL.md");
        doc.Body.Should().Be("# Adding a migration\n1. dotnet ef migrations add <Name>");
        doc.IsActive.Should().BeTrue();
        doc.UpdatedAt.Should().Be(Now);
        doc.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void The_hash_ignores_line_endings_but_not_content()
    {
        var lf = Parse(Valid).Value.ContentHash;
        var crlf = Parse(Valid.ReplaceLineEndings("\r\n")).Value.ContentHash;
        crlf.Should().Be(lf, "a CRLF checkout must not re-sync every skill");
        Parse(Valid.ReplaceLineEndings("\r\n")).Value.Body.Should().Be(Parse(Valid).Value.Body, "the body is stored LF-normalised");
        Parse(Valid.Replace("EF Core", "EF", StringComparison.Ordinal)).Value.ContentHash.Should().NotBe(lf);
    }

    [Fact]
    public void A_leading_BOM_and_a_single_blank_line_after_the_frontmatter_are_absorbed()
    {
        Parse("\uFEFF" + Valid).IsSuccess.Should().BeTrue();
        Parse(Valid.Replace("---\n\n# Adding", "---\n# Adding", StringComparison.Ordinal)).Value.Body
            .Should().Be("# Adding a migration\n1. dotnet ef migrations add <Name>");
    }

    [Theory]
    [InlineData("no frontmatter at all", "missing YAML frontmatter")]
    [InlineData("---\nname: dotnet-migrations\ndescription: x\n", "unterminated YAML frontmatter")]
    [InlineData("---\n  name: dotnet-migrations\n---\nbody", "indented YAML is not supported")]
    [InlineData("---\nname dotnet-migrations\n---\nbody", "must be `key: value`")]
    [InlineData("---\nName: dotnet-migrations\n---\nbody", "invalid frontmatter key")]
    [InlineData("---\nauthor: me\n---\nbody", "unknown frontmatter key")]
    [InlineData("---\nname: a\nname: b\n---\nbody", "duplicate frontmatter key")]
    [InlineData("---\ndescription: x\n---\nbody", "missing the required key 'name'")]
    [InlineData("---\nname: dotnet-migrations\n---\nbody", "missing the required key 'description'")]
    [InlineData("---\nname: dotnet-migrations\ndescription:\n---\nbody", "'description' has no value")]
    [InlineData("---\nname: dotnet-migrations\ndescription: x # why\n---\nbody", "contains a comment")]
    [InlineData("---\nname: dotnet-migrations\ndescription: |\n---\nbody", "block scalars, anchors and flow mappings")]
    [InlineData("---\nname: dotnet-migrations\ndescription: \"unterminated\n---\nbody", "unterminated quoted value")]
    [InlineData("---\nname: dotnet-migrations\ndescription: \"bad \\q escape\"\n---\nbody", "unsupported escape")]
    [InlineData("---\nname: dotnet-migrations\ndescription: x\ntags:\n  - dotnet\n---\nbody", "flow sequence")]
    [InlineData("---\nname: dotnet-migrations\ndescription: x\ntags: [a, [b]]\n---\nbody", "nested sequences")]
    [InlineData("---\nname: Dotnet Migrations\ndescription: x\n---\nbody", "not a valid skill name")]
    [InlineData("---\nname: something-else\ndescription: x\n---\nbody", "does not match the file or folder name")]
    [InlineData("---\nname: dotnet-migrations\ndescription: x\n---\n\n", "Body")]
    public void Malformed_input_is_rejected_with_a_reason_naming_the_file(string text, string expectedFragment)
    {
        var result = Parse(text);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SkillValidationFailed);
        result.Error.Message.Should().StartWith("dotnet-migrations/SKILL.md: ").And.Contain(expectedFragment);
    }

    [Fact]
    public void Comments_blank_lines_and_quoted_scalars_are_accepted()
    {
        var text = "---\n# a comment\n\nname: 'dotnet-migrations'\ndescription: \"He said \\\"hi\\\": it's fine\"\ntags: [ 'A', \"b\" , c ]\n---\nbody\n";
        var doc = Parse(text).Value;
        doc.Name.Value.Should().Be("dotnet-migrations");
        doc.Description.Should().Be("He said \"hi\": it's fine");
        doc.Tags.Should().Equal(["a", "b", "c"], "tags are normalised to lower case");
    }

    [Fact]
    public void An_empty_tags_value_and_an_empty_flow_sequence_both_mean_no_tags()
    {
        Parse("---\nname: dotnet-migrations\ndescription: x\ntags:\n---\nbody").Value.Tags.Should().BeEmpty();
        Parse("---\nname: dotnet-migrations\ndescription: x\ntags: []\n---\nbody").Value.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Limits_from_SkillRules_are_reported_against_the_file()
    {
        var tooLong = "---\nname: dotnet-migrations\ndescription: " + new string('d', SkillDocument.MaxDescriptionLength + 1) + "\n---\nbody";
        Parse(tooLong).Error.Message.Should().StartWith("dotnet-migrations/SKILL.md: ").And.Contain("Description");

        var bigBody = "---\nname: dotnet-migrations\ndescription: x\n---\n" + new string('b', SkillDocument.MaxBodyChars + 1);
        Parse(bigBody).Error.Message.Should().Contain("Body");
    }
}
