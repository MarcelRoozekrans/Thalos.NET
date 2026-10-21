using System.Text.Json;
using System.Text.Json.Nodes;
using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>
/// The JSON-shaped variable contract: every entry point a variable bag comes in through refuses a value that is not
/// a string, number, bool, null, <see cref="JsonElement"/>, <see cref="JsonNode"/> or a list or dictionary of those,
/// naming the key or path and the type; an allowed value is written exactly as the reflection-based serializer
/// wrote it; and a string still renders raw in a node's prompt block.
/// </summary>
public sealed class WorkflowVariableContractTests
{
    private sealed record Point(int X, int Y);

    /// <summary>A non-JSON value, the key or path the refusal must name, and the type it must name.</summary>
    private static (object? Value, string Path, string TypeName) Case(string name) => name switch
    {
        "guid" => (new Guid(0x0f8fad5b, 0xd9cb, 0x469f, 0xa1, 0x65, 0x70, 0x86, 0x77, 0x28, 0x95, 0x0e), "v", typeof(Guid).FullName!),
        "datetime" => (new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc), "v", typeof(DateTime).FullName!),
        "enum" => (DayOfWeek.Monday, "v", typeof(DayOfWeek).FullName!),
        "record" => (new Point(1, 2), "v", typeof(Point).FullName!),
        "guid-in-list" => (new List<object?> { "a", "b", Guid.Empty }, "v[2]", typeof(Guid).FullName!),
        "datetime-in-map" => (new Dictionary<string, object?>(StringComparer.Ordinal) { ["owner"] = DateTime.UnixEpoch }, "v.owner", typeof(DateTime).FullName!),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    public static TheoryData<string, string> Refusals()
    {
        var data = new TheoryData<string, string>();
        foreach (var value in new[] { "guid", "datetime", "enum", "record", "guid-in-list", "datetime-in-map" })
        {
            foreach (var entry in new[] { "NodeResult", "HostActionResult", "WorkflowStartRequest", "WorkflowRunStartOptions" })
            {
                data.Add(value, entry);
            }
        }

        return data;
    }

    private static object Enter(string entry, IReadOnlyDictionary<string, object?> bag) => entry switch
    {
        "NodeResult" => new NodeResult("done", bag),
        "HostActionResult" => new HostActionResult("done", bag),
        "WorkflowStartRequest" => new WorkflowStartRequest
        {
            Process = "p",
            Version = 1,
            CorrelationKey = "c",
            StartNode = "start",
            StartedBy = new RunPrincipal("alice", []),
            InitialVariables = bag,
        },
        "WorkflowRunStartOptions" => new WorkflowRunStartOptions
        {
            Process = "p",
            CorrelationKey = "c",
            StartedBy = new RunPrincipal("alice", []),
            Variables = bag,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, null),
    };

    // Red: make WorkflowVariableJson.Validate a no-op; no entry point throws.
    // Red: drop the Validate call from any one entry point; that entry point's rows stop throwing.
    // Red: in WorkflowVariableJson's refusal message, write the root key instead of the path; the nested rows fail.
    // Red: drop the index segment from a list element's path; the "guid-in-list" rows then name "v".
    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_non_JSON_variable_is_refused_naming_its_path_and_type(string value, string entry)
    {
        var (bad, path, typeName) = Case(value);
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ok"] = "fine", ["v"] = bad };

        var act = () => Enter(entry, bag);

        act.Should().Throw<ArgumentException>()
            .WithMessage($"Workflow variable '{path}' has type '{typeName}', which is not JSON-shaped. Allowed: string, number, bool, null, JsonElement, JsonNode, and lists or dictionaries of those.*");
    }

    // Red: make Validate refuse a JsonNode as well as a Guid; the first entry point then throws on "node".
    [Fact]
    public void Every_JSON_shape_is_accepted_at_every_entry_point()
    {
        var bag = EveryShape();

        foreach (var entry in new[] { "NodeResult", "HostActionResult", "WorkflowStartRequest", "WorkflowRunStartOptions" })
        {
            var act = () => Enter(entry, bag);
            act.Should().NotThrow(entry);
        }
    }

    // Red: in WorkflowVariableJson.Write, write a float through its double overload; "f" then reads 1.100000023841858.
    // Red: in Write, take only JsonValue as a leaf, not every JsonNode; the JsonObject in "node" then enumerates as
    // KeyValuePair elements and is refused.
    // Red: in Write, sort a non-generic dictionary's keys; "dict" then reads {"a":2,"z":1}.
    [Fact]
    public void Every_JSON_shape_is_written_as_the_reflective_serializer_wrote_it()
    {
        var bag = EveryShape();

        var written = WorkflowVariableJson.ToElement(bag).GetRawText();

        // The reflection-based serializer, kept in the test as the oracle for today's bytes.
        written.Should().Be(JsonSerializer.Serialize(bag));
    }

    // Red: in WorkflowVariableJson.ToElement, start the path at "value" instead of the root; the message then names "value".
    [Fact]
    public void ToElement_on_a_bare_non_JSON_value_names_the_root_path()
    {
        var act = () => WorkflowVariableJson.ToElement(Guid.Empty);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Workflow variable '$' has type 'System.Guid', which is not JSON-shaped.*");
    }

    // Red: in WorkflowVariableJson's path for a list element, drop the index; the message then names "$".
    [Fact]
    public void ToElement_on_a_list_names_the_index_from_the_root()
    {
        var act = () => WorkflowVariableJson.ToElement(new List<object?> { 1, 2, DayOfWeek.Friday });

        act.Should().Throw<ArgumentException>()
            .WithMessage("Workflow variable '[2]' has type 'System.DayOfWeek', which is not JSON-shaped.*");
    }

    // Red: in WorkflowVariableBlock.RenderValue, route the string branch through
    // WorkflowVariableJson.ToElement(s).GetRawText(); the value then renders quoted and escaped as "a"b".
    [Fact]
    public void A_string_variable_renders_raw_in_the_prompt_block()
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["q"] = "a\"b" };

        var block = WorkflowVariableBlock.Render(bag).Block;

        block.Should().Contain("\nq: a\"b\n");
    }

    // Red: in WorkflowVariableBlock.RenderValue, drop the JsonObject branch; the object then renders through the
    // sequence branch as KeyValuePair elements, which the contract refuses.
    [Fact]
    public void A_JsonObject_variable_renders_as_a_JSON_object_in_the_prompt_block()
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["o"] = new JsonObject { ["k"] = "v" } };

        var block = WorkflowVariableBlock.Render(bag).Block;

        block.Should().Contain("\no: {\"k\":\"v\"}\n");
    }

    /// <summary>Every allowed shape, including the ones a host passes in rather than the engine's own read shapes.</summary>
    private static Dictionary<string, object?> EveryShape() => new(StringComparer.Ordinal)
    {
        ["s"] = "a\"b <a & 'é' 🚀>\nline",
        ["b"] = true,
        ["z"] = null,
        ["by"] = (byte)200,
        ["sb"] = (sbyte)-8,
        ["sh"] = (short)-300,
        ["us"] = (ushort)60000,
        ["i"] = -7,
        ["ui"] = 4000000000u,
        ["n"] = 3L,
        ["ul"] = ulong.MaxValue,
        ["f"] = 1.1f,
        ["d"] = 1.5,
        ["whole"] = 2.0,
        ["m"] = 12.50m,
        ["l"] = new List<string> { "x", "y" },
        ["arr"] = new[] { 1, 2 },
        ["o"] = new List<object?> { "x", 1L, false, null },
        ["dict"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["z"] = 1, ["a"] = 2 },
        ["map"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = "v", ["inner"] = new List<object?> { 1.25 } },
        ["e"] = JsonDocument.Parse("""{"x":[1,2],"y":"é"}""").RootElement.Clone(),
        ["node"] = new JsonObject { ["k"] = "v", ["a"] = new JsonArray(1, "é") },
    };
}
