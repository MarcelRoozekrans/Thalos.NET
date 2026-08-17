using System.Text.Json;

namespace Thalos.Tests.Unit.Abstractions;

public sealed class MemoryAbstractionsTests
{
    [Fact]
    public void MemoryId_roundtrips_and_is_a_distinct_type()
    {
        var id = MemoryId.New();
        MemoryId.Parse(id.ToString(), null).Should().Be(id);
        JsonSerializer.Deserialize<MemoryId>(JsonSerializer.Serialize(id)).Should().Be(id);
        id.ToString().Should().HaveLength(26);
        typeof(MemoryId).Should().NotBe<SessionId>();
    }

    [Fact]
    public void Memory_error_factories_set_codes()
    {
        var id = MemoryId.New();
        AgentError.MemoryNotFound(id).Code.Should().Be(AgentErrorCode.MemoryNotFound);
        AgentError.MemoryNotFound(id).Message.Should().Contain(id.ToString());
        AgentError.MemoryForbidden(id).Code.Should().Be(AgentErrorCode.MemoryForbidden);
        AgentError.MemoryStoreFailed("x", "Npgsql").Should().Be(new AgentError(AgentErrorCode.MemoryStoreFailed, "x", "Npgsql"));
        AgentError.MemoryIndexUnavailable("x").Code.Should().Be(AgentErrorCode.MemoryIndexUnavailable);
        AgentError.MemoryIndexFailed("x").Code.Should().Be(AgentErrorCode.MemoryIndexFailed);
        AgentError.MemoryValidationFailed("Text is required.").Code.Should().Be(AgentErrorCode.MemoryValidationFailed);
    }

    [Fact]
    public void Memory_events_have_stable_kinds()
    {
        var s = SessionId.New(); var t = TurnId.New(); var m = MemoryId.New();
        new MemoryRecalledEvent(s, t, [m], 42).Kind.Should().Be("memory-recalled");
        new MemoryRecalledEvent(s, t, [m], 42).Count.Should().Be(1);
        new MemoryStoredEvent(s, t, m, "fact", Deduped: false).Kind.Should().Be("memory-stored");
        new MemoryRecallFailedEvent(s, t, AgentErrorCode.MemoryIndexUnavailable).Kind.Should().Be("memory-recall-failed");
        new MemoryIndexPendingEvent(s, t, m).Kind.Should().Be("memory-index-pending");
        new MemoryQuarantinedEvent(s, t, m, "High: SEC-01").Kind.Should().Be("memory-quarantined");
    }

    [Fact]
    public void AgentDefinition_memory_settings_default_to_null_and_compare_by_value()
    {
        var def = new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "i" };
        def.Memory.Should().BeNull();
        new AgentMemorySettings { Enabled = false, TopK = 3 }.Should().Be(new AgentMemorySettings { Enabled = false, TopK = 3 });
        new AgentDefinitionValidator().Validate(def with { Memory = new AgentMemorySettings { TopK = 2 } }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Verdicts_are_allow_or_quarantine()
    {
        UntrustedContentVerdict.Allow().Allowed.Should().BeTrue();
        UntrustedContentVerdict.Quarantine("High: SEC-01").Should().Be(new UntrustedContentVerdict(false, "High: SEC-01"));
        default(UntrustedContentVerdict).Allowed.Should().BeFalse("default is a denial — fail closed");
    }
}
