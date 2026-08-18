namespace Thalos.Tests.Unit.Abstractions;

public sealed class SkillAbstractionsTests
{
    [Fact]
    public void Skill_error_factories_carry_the_code_and_a_safe_message()
    {
        var notFound = AgentError.SkillNotFound("dotnet-migrations");
        notFound.Code.Should().Be(AgentErrorCode.SkillNotFound);
        notFound.Message.Should().Contain("dotnet-migrations");
        notFound.Detail.Should().BeNull();

        AgentError.SkillStoreFailed("boom", "IOException").Should().Be(new AgentError(AgentErrorCode.SkillStoreFailed, "boom", "IOException"));
        AgentError.SkillValidationFailed("bad").Code.Should().Be(AgentErrorCode.SkillValidationFailed);
        AgentError.SkillSearchUnavailable("no generator").Code.Should().Be(AgentErrorCode.SkillSearchUnavailable);
    }

    [Fact]
    public void SkillCatalogueFailedEvent_has_a_stable_kind()
    {
        var e = new SkillCatalogueFailedEvent(SessionId.New(), TurnId.New(), AgentErrorCode.SkillStoreFailed);
        e.Kind.Should().Be("skill-catalogue-failed");
        e.Kind.Should().Be(AgentEventKinds.SkillCatalogueFailed);
        AgentEvent.KindOf(typeof(SkillCatalogueFailedEvent)).Should().Be(AgentEventKinds.SkillCatalogueFailed);
    }

    [Fact]
    public void AgentDefinition_has_no_skills_by_default_and_keeps_the_globs_it_is_given()
    {
        var bare = new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "i" };
        bare.Skills.Should().BeEmpty("a catalogue costs tokens on every turn, so skills are opt-in unlike tools");
        bare.Tools.Should().Equal(["*"], "the tool default is unchanged");

        var scoped = bare with { Skills = ["release", "dotnet-*"] };
        scoped.Skills.Should().Equal(["release", "dotnet-*"]);
    }
}
