using System.Globalization;
using System.Text;
using System.Text.Json;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Golden output of every <see cref="OrmWorkflowJson"/> method, captured from the reflection-based serializer before
/// the store moves to generated metadata. Each write test compares the exact text, or for the dispatch payload the
/// exact bytes, a column or the outbox receives; each read test parses a literal of that text and compares the value.
/// These are characterization tests: they pass on today's code, and each one's Red names the change that breaks it.
/// </summary>
public sealed class OrmWorkflowJsonGoldenTests
{
    /// <summary>Text with every character class the encoder treats differently: markup, quotes, accents, an emoji and a newline.</summary>
    private const string Tricky = "a\"b <a & 'é' 🚀>\nline";

    private static readonly AgentId Agent = AgentId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV", CultureInfo.InvariantCulture);

    private static readonly RunPrincipal Principal = new("alice", ["admin", "dev"]) { DisplayName = "Alice <é>" };

    private static RunManifest Manifest() => new()
    {
        Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal)
        {
            ["implement"] = new NodePin("implementer", Agent, "rev-1", "manufacture-implement", "hash-1"),
            ["review"] = new NodePin("reviewer", Agent, null, "code-review", "hash-2"),
        },
        Documents = new Dictionary<string, string>(StringComparer.Ordinal) { ["standing_instructions"] = Tricky },
    };

    /// <summary>A variable of every shape a node or a host reports: the engine's own read shapes and the ones a host passes in.</summary>
    private static Dictionary<string, object?> Variables() => new(StringComparer.Ordinal)
    {
        ["s"] = Tricky,
        ["n"] = 3L,
        ["i"] = 7,
        ["d"] = 1.5,
        ["b"] = true,
        ["z"] = null,
        ["l"] = new List<string> { "x", "y" },
        ["o"] = new List<object?> { "x", 1L, false, null },
        ["m"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = "v", ["n"] = 2L, ["inner"] = new List<object?> { 1.25 } },
        ["e"] = JsonDocument.Parse("""{"x":[1,2],"y":"é"}""").RootElement.Clone(),
    };

    // Red: give SerializeVisits options with WriteIndented = true; the text gains line breaks.
    [Fact]
    public void Visits_are_written_as_before() =>
        OrmWorkflowJson.SerializeVisits(new Dictionary<string, int>(StringComparer.Ordinal) { ["implement"] = 1, ["review"] = 2 })
            .Should().Be("""
                {"implement":1,"review":2}
                """);

    // Red: in DeserializeVisits, deserialize "{}" instead of the column text; the dictionary comes back empty.
    [Fact]
    public void Visits_are_read_as_before() =>
        OrmWorkflowJson.DeserializeVisits("""{"implement":1,"review":2}""")
            .Should().Equal(new Dictionary<string, int>(StringComparer.Ordinal) { ["implement"] = 1, ["review"] = 2 });

    // Red: give SerializeVariables options with Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping; the markup,
    // quote, accent and emoji in "s" and the accent in "e" are then written unescaped.
    [Fact]
    public void Variables_of_every_shape_are_written_as_before() =>
        OrmWorkflowJson.SerializeVariables(Variables()).Should().Be("""
            {"s":"a\u0022b \u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E\nline","n":3,"i":7,"d":1.5,"b":true,"z":null,"l":["x","y"],"o":["x",1,false,null],"m":{"k":"v","n":2,"inner":[1.25]},"e":{"x":[1,2],"y":"\u00E9"}}
            """);

    // Red: in DeserializeVariables, return JsonSerializer.Deserialize<Dictionary<string, object?>>(json); every value
    // is then a boxed JsonElement and the type assertions fail.
    [Fact]
    public void Variables_are_read_back_as_plain_CLR_values()
    {
        var read = OrmWorkflowJson.DeserializeVariables("""
            {"s":"a\u0022b \u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E\nline","n":3,"i":7,"d":1.5,"b":true,"z":null,"l":["x","y"],"o":["x",1,false,null],"m":{"k":"v","n":2,"inner":[1.25]},"e":{"x":[1,2],"y":"\u00E9"}}
            """);

        read.Keys.Should().Equal("s", "n", "i", "d", "b", "z", "l", "o", "m", "e");
        read["s"].Should().BeOfType<string>().Which.Should().Be(Tricky);
        read["n"].Should().BeOfType<long>().Which.Should().Be(3L);
        read["i"].Should().BeOfType<long>().Which.Should().Be(7L);
        read["d"].Should().BeOfType<double>().Which.Should().Be(1.5);
        read["b"].Should().BeOfType<bool>().Which.Should().BeTrue();
        read["z"].Should().BeNull();
        read["l"].Should().BeOfType<List<object?>>().Which.Should().Equal("x", "y");
        read["o"].Should().BeOfType<List<object?>>().Which.Should().Equal("x", 1L, false, null);
        var map = read["m"].Should().BeOfType<Dictionary<string, object?>>().Subject;
        map.Keys.Should().Equal("k", "n", "inner");
        map["k"].Should().BeOfType<string>().Which.Should().Be("v");
        map["n"].Should().BeOfType<long>().Which.Should().Be(2L);
        map["inner"].Should().BeOfType<List<object?>>().Which.Should().Equal(1.25);
        var element = read["e"].Should().BeOfType<Dictionary<string, object?>>().Subject;
        element["x"].Should().BeOfType<List<object?>>().Which.Should().Equal(1L, 2L);
        element["y"].Should().Be("é");
    }

    // Red: drop JsonSerializerDefaults.Web from OrmWorkflowJson's ManifestJsonOptions; the property names are then
    // written PascalCase.
    [Fact]
    public void A_manifest_is_written_as_before() =>
        OrmWorkflowJson.SerializeManifest(Manifest()).Should().Be("""
            {"nodes":{"implement":{"agentName":"implementer","agentId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","agentRevision":"rev-1","skillName":"manufacture-implement","skillHash":"hash-1"},"review":{"agentName":"reviewer","agentId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","agentRevision":null,"skillName":"code-review","skillHash":"hash-2"}},"documents":{"standing_instructions":"a\u0022b \u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E\nline"}}
            """);

    // Red: drop JsonSerializerDefaults.Web from ManifestJsonOptions; the camelCase names no longer bind and the read
    // fails.
    [Fact]
    public void A_manifest_is_read_as_before()
    {
        var read = OrmWorkflowJson.DeserializeManifest("""
            {"nodes":{"implement":{"agentName":"implementer","agentId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","agentRevision":"rev-1","skillName":"manufacture-implement","skillHash":"hash-1"},"review":{"agentName":"reviewer","agentId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","agentRevision":null,"skillName":"code-review","skillHash":"hash-2"}},"documents":{"standing_instructions":"a\u0022b \u003Ca \u0026 \u0027\u00E9\u0027 \uD83D\uDE80\u003E\nline"}}
            """);

        read.Should().BeEquivalentTo(Manifest());
    }

    // Red: drop JsonSerializerDefaults.Web from ManifestJsonOptions; the names are then written PascalCase.
    [Fact]
    public void A_principal_is_written_as_before() =>
        OrmWorkflowJson.SerializePrincipal(Principal).Should().Be("""
            {"id":"alice","roles":["admin","dev"],"displayName":"Alice \u003C\u00E9\u003E"}
            """);

    // Red: drop JsonSerializerDefaults.Web from ManifestJsonOptions; the camelCase names no longer bind.
    [Fact]
    public void A_principal_is_read_as_before() =>
        OrmWorkflowJson.DeserializePrincipal("""{"id":"alice","roles":["admin","dev"],"displayName":"Alice \u003C\u00E9\u003E"}""")
            .Should().BeEquivalentTo(Principal);

    // Red: drop JsonSerializerDefaults.Web from ManifestJsonOptions; the names are then written PascalCase.
    [Fact]
    public void Usage_is_written_as_before() =>
        OrmWorkflowJson.SerializeUsage(new TurnUsage(120, 45, "claude-test"))
            .Should().Be("""
                {"inputTokens":120,"outputTokens":45,"modelId":"claude-test","cacheReadTokens":0,"cacheWriteTokens":0}
                """);

    // Red: drop JsonSerializerDefaults.Web from ManifestJsonOptions; the camelCase names no longer bind.
    [Fact]
    public void Usage_is_read_as_before() =>
        OrmWorkflowJson.DeserializeUsage("""{"inputTokens":120,"outputTokens":45,"modelId":"claude-test","cacheReadTokens":0,"cacheWriteTokens":0}""")
            .Should().Be(new TurnUsage(120, 45, "claude-test"));

    // Red: drop JsonSerializerDefaults.Web from ManifestJsonOptions; the names are then written PascalCase.
    [Fact]
    public void A_resume_envelope_is_written_as_before() =>
        OrmWorkflowJson.SerializeResumeEnvelope(new LastResumeEnvelope(Principal, "approved"))
            .Should().Be("""
                {"principal":{"id":"alice","roles":["admin","dev"],"displayName":"Alice \u003C\u00E9\u003E"},"signal":"approved"}
                """);

    // Red: drop JsonSerializerDefaults.Web from ManifestJsonOptions; the camelCase names no longer bind.
    [Fact]
    public void A_resume_envelope_is_read_as_before() =>
        OrmWorkflowJson.DeserializeResumeEnvelope("""{"principal":{"id":"alice","roles":["admin","dev"],"displayName":"Alice \u003C\u00E9\u003E"},"signal":"approved"}""")
            .Should().BeEquivalentTo(new LastResumeEnvelope(Principal, "approved"));

    // Red: give SerializeDispatch JsonSerializerDefaults.Web options; the payload's names are then camelCase.
    [Fact]
    public void The_dispatch_payload_bytes_are_as_before()
    {
        var bytes = OrmWorkflowJson.SerializeDispatch(new WorkflowDispatchMessage(new Guid(0x0f8fad5b, 0xd9cb, 0x469f, 0xa1, 0x65, 0x70, 0x86, 0x77, 0x28, 0x95, 0x0e), 7, "review"));

        bytes.Should().Equal(Encoding.UTF8.GetBytes("""
            {"RunId":"0f8fad5b-d9cb-469f-a165-70867728950e","Seq":7,"Node":"review"}
            """));
    }
}
