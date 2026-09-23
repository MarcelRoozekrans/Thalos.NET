using Thalos.Skills.Charters;

namespace Thalos.Tests.Skills.Charters;

public sealed class RoleCharterFileLoaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private const string Valid = """
        ---
        name: reviewer
        description: Reviews work it did not write.
        model: claude-opus-5
        skills: [manufacture-review, manufacture-retrospect]
        ---

        You review a change you did not make.
        """;

    [Fact]
    public void Parses_prose_model_and_skills()
    {
        var parsed = RoleCharterFileLoader.Parse("roles/reviewer.md", "reviewer", Valid, Now);

        parsed.IsSuccess.Should().BeTrue(parsed.IsFailure ? parsed.Error.ToString() : "");
        parsed.Value.Role.Should().Be("reviewer");
        parsed.Value.Model.Should().Be("claude-opus-5");
        parsed.Value.Skills.Should().Equal("manufacture-review", "manufacture-retrospect");
        parsed.Value.Instructions.Should().Be("You review a change you did not make.");
        parsed.Value.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void A_charter_naming_tools_is_rejected_with_a_message_that_says_why()
    {
        var withTools = Valid.Replace("model: claude-opus-5", "tools: [roslyn__apply_code_action]", StringComparison.Ordinal);

        var parsed = RoleCharterFileLoader.Parse("roles/reviewer.md", "reviewer", withTools, Now);

        parsed.IsFailure.Should().BeTrue();
        parsed.Error.Message.Should().Contain("tools").And.Contain("configuration");
    }

    [Fact]
    public void Name_must_match_the_file()
    {
        var parsed = RoleCharterFileLoader.Parse("roles/implementer.md", "implementer", Valid, Now);
        parsed.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Line_endings_do_not_change_the_hash()
    {
        var lf = RoleCharterFileLoader.Parse("a", "reviewer", Valid, Now).Value.ContentHash;
        var crlf = RoleCharterFileLoader.Parse("a", "reviewer", Valid.ReplaceLineEndings("\r\n"), Now).Value.ContentHash;
        crlf.Should().Be(lf);
    }
}
