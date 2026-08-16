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
    public void Ids_are_distinct_types()
    {
        typeof(AgentId).Should().NotBe<SessionId>();
        typeof(TurnId).Should().NotBe<ToolCallId>();
    }

    [Fact]
    public void TryParse_rejects_garbage()
    {
        SessionId.TryParse("not-an-id", null, out _).Should().BeFalse();
    }
}
