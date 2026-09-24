namespace Thalos.Tests.Skills.Charters;

/// <summary>A minimal, valid config <see cref="AgentDefinition"/> for tests that need one but don't care about its shape.</summary>
internal static class TestAgents
{
    public static AgentDefinition Definition() => new()
    {
        Id = AgentId.New(),
        Name = "agent",
        Instructions = "Be helpful.",
    };
}
