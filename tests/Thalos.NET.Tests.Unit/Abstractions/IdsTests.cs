using System.Text.Json;

namespace Thalos.Tests.Unit.Abstractions;

public sealed class IdsTests
{
    [Fact]
    public void SessionId_roundtrips_through_string_and_json()
    {
        var id = SessionId.New();

        SessionId.Parse(id.ToString(), null).Should().Be(id);
        JsonSerializer.Deserialize<SessionId>(JsonSerializer.Serialize(id)).Should().Be(id);
        id.ToString().Should().HaveLength(26); // ULID base32
    }

    [Fact]
    public void All_id_types_roundtrip_through_json_inside_a_record()
    {
        var record = new AgentSessionRecord(
            SessionId.New(), AgentId.New(), "owner", SessionState.Idle,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, 0);
        var call = new ToolCallSummary(ToolCallId.New(), "t", "{}", true, null, TimeSpan.Zero);
        var result = new AgentTurnResult(TurnId.New(), record.Id, "hi", TurnUsage.Empty("m"), [call], TimeSpan.Zero);

        JsonSerializer.Deserialize<AgentSessionRecord>(JsonSerializer.Serialize(record)).Should().Be(record);
        var back = JsonSerializer.Deserialize<AgentTurnResult>(JsonSerializer.Serialize(result))!;
        back.TurnId.Should().Be(result.TurnId);
        back.SessionId.Should().Be(result.SessionId);
        back.ToolCalls.Should().ContainSingle().Which.Should().Be(call);
    }

    [Fact]
    public void TryParse_rejects_garbage()
    {
        SessionId.TryParse("not-an-id", null, out _).Should().BeFalse();
    }
}
