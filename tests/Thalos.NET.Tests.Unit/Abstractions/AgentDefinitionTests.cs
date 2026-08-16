namespace Thalos.Tests.Unit.Abstractions;

public sealed class AgentDefinitionTests
{
    private static AgentDefinition Valid() => new()
    {
        Id = AgentId.New(),
        Name = "architect",
        Instructions = "You are helpful.",
    };

    [Fact]
    public void Valid_definition_passes_validation()
    {
        var result = new AgentDefinitionValidator().Validate(Valid());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_name_and_instructions_fail_validation()
    {
        var result = new AgentDefinitionValidator().Validate(Valid() with { Name = "", Instructions = "" });
        result.IsValid.Should().BeFalse();
        result.Failures.ToArray().Select(f => f.PropertyName).Should().BeEquivalentTo(["Name", "Instructions"]);
    }

    [Fact]
    public void Tools_defaults_to_wildcard()
    {
        Valid().Tools.Should().BeEquivalentTo(["*"]);
    }
}
