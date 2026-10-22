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
    // Red: drop the byte[] arm from WorkflowVariableJson.TryWriteLeaf; "bytes" is then written [1,2,3], not "AQID".
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

    // Red: drop the depth check from WorkflowVariableJson.Walk; Validate then overflows the stack on the list that
    // contains itself.
    [Fact]
    public void A_list_that_contains_itself_is_refused_as_too_deep()
    {
        var loop = new List<object?>();
        loop.Add(loop);
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["v"] = loop };

        var act = () => new NodeResult("done", bag);

        act.Should().Throw<ArgumentException>().WithMessage("Workflow variable 'v[0]*' nests deeper than 64 levels.*");
    }

    // Red: lower MaxDepth in WorkflowVariableJson to 63; 63 nested lists are then refused.
    [Fact]
    public void A_variable_of_63_nested_lists_is_accepted()
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["v"] = Nested(63) };

        var act = () => new NodeResult("done", bag);

        act.Should().NotThrow();
    }

    // Red: raise MaxDepth in WorkflowVariableJson to 65; 64 nested lists are then accepted.
    [Fact]
    public void A_variable_of_64_nested_lists_is_refused()
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["v"] = Nested(64) };

        var act = () => new NodeResult("done", bag);

        act.Should().Throw<ArgumentException>().WithMessage("Workflow variable 'v*' nests deeper than 64 levels.*");
    }

    // The leaf tests below put the leaf inside one list, so the variable holds 1 + the leaf's own levels. As with
    // the lists above, a variable may hold 63 levels: the bag is the 64th.

    // Red: drop the LeafDepth check from WorkflowVariableJson.Walk; the element is then accepted and only the
    // reader, parsing it back, throws a JsonException.
    [Fact]
    public void A_JsonElement_leaf_too_deep_for_the_limit_is_refused_naming_its_path()
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["v"] = new List<object?> { "a", DeepElement(63) } };

        var act = () => new NodeResult("done", bag);

        act.Should().Throw<ArgumentException>().WithMessage("Workflow variable 'v[1]' nests deeper than 64 levels.*");
    }

    // Red: in WorkflowVariableJson.LeafDepth, return 0 for a JsonNode; the node is then accepted.
    [Fact]
    public void A_JsonNode_leaf_too_deep_for_the_limit_is_refused_naming_its_path()
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["v"] = new List<object?> { DeepNode(63) } };

        var act = () => new NodeResult("done", bag);

        act.Should().Throw<ArgumentException>().WithMessage("Workflow variable 'v[0]' nests deeper than 64 levels.*");
    }

    // Red: in WorkflowVariableJson.Walk, give LeafDepth one level less of budget; both leaves are then refused.
    [Fact]
    public void JSON_leaves_that_bring_a_variable_to_exactly_63_levels_are_accepted_and_written()
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["e"] = new List<object?> { DeepElement(62) },
            ["n"] = new List<object?> { DeepNode(62) },
        };

        var act = () => new NodeResult("done", bag);

        act.Should().NotThrow();
        WorkflowVariableJson.ToElement(bag).GetProperty("e")[0].ValueKind.Should().Be(JsonValueKind.Array);
    }

    // Red: in WorkflowVariableJson.ElementDepth, count only arrays; the nested objects are then accepted.
    [Fact]
    public void ToElement_refuses_a_JsonElement_of_nested_objects_too_deep_before_writing()
    {
        // A bare value has no bag above it, so 64 levels are allowed and 65 are not.
        var json = string.Concat(Enumerable.Repeat("{\"k\":", 65)) + "1" + new string('}', 65);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });

        var act = () => WorkflowVariableJson.ToElement(document.RootElement);

        act.Should().Throw<ArgumentException>().WithMessage("Workflow variable '$' nests deeper than 64 levels.*");
    }

    /// <summary>A <see cref="JsonElement"/> of <paramref name="levels"/> nested arrays, parsed past the default limit.</summary>
    private static JsonElement DeepElement(int levels)
    {
        var json = new string('[', levels) + new string(']', levels);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        return document.RootElement.Clone();
    }

    /// <summary>A <see cref="JsonArray"/> of <paramref name="levels"/> nested arrays.</summary>
    private static JsonArray DeepNode(int levels)
    {
        var node = new JsonArray();
        for (var i = 1; i < levels; i++)
        {
            node = new JsonArray(node);
        }

        return node;
    }

    /// <summary><paramref name="levels"/> lists, each the only member of the one around it.</summary>
    private static List<object?> Nested(int levels)
    {
        var list = new List<object?>();
        for (var i = 1; i < levels; i++)
        {
            list = [list];
        }

        return list;
    }

    // Red: drop the finite check from WorkflowVariableJson.Walk; Validate then lets the value through.
    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void A_non_finite_double_is_refused_naming_its_path(double value, string text)
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["v"] = new List<object?> { 1.5, value } };

        var act = () => new NodeResult("done", bag);

        act.Should().Throw<ArgumentException>()
            .WithMessage($"Workflow variable 'v[1]' is {text}, which is not a JSON number. A float or double must be finite.*");
    }

    // Red: in the finite check in WorkflowVariableJson.Walk, test only double; the float NaN is then let through.
    [Fact]
    public void A_non_finite_float_is_refused()
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["v"] = float.NaN };

        var act = () => new NodeResult("done", bag);

        act.Should().Throw<ArgumentException>().WithMessage("Workflow variable 'v' is NaN, which is not a JSON number.*");
    }

    // Red: drop the finite check from WorkflowVariableJson.Walk; Write then throws the writer's own exception
    // part-way through the object, not this message.
    [Fact]
    public void ToElement_refuses_a_non_finite_double_before_writing()
    {
        var act = () => WorkflowVariableJson.ToElement(new Dictionary<string, object?>(StringComparer.Ordinal) { ["ok"] = 1, ["x"] = double.NaN });

        act.Should().Throw<ArgumentException>().WithMessage("Workflow variable 'x' is NaN, which is not a JSON number.*");
    }

    // Red: in WorkflowVariableJson.WalkSequence, return refusal.Under(index) for a KeyValuePair member too; the
    // message then names 'v[0]' and KeyValuePair`2 instead of the dictionary.
    [Fact]
    public void A_custom_generic_dictionary_is_refused_naming_its_own_type()
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["v"] = new CustomMap { ["k"] = 1 } };

        var act = () => new NodeResult("done", bag);

        act.Should().Throw<ArgumentException>()
            .WithMessage($"Workflow variable 'v' has type '{typeof(CustomMap).FullName}', which is not JSON-shaped.*");
    }

    // Red: in WorkflowVariableBlock.RenderMap, render each key as Sanitize(keys[kept]) rather than through
    // WorkflowVariableJson.ToElement; the key then renders raw and unquoted.
    [Fact]
    public void A_nested_map_key_renders_as_an_escaped_JSON_string_in_the_prompt_block()
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["m"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["<k é>"] = "v" },
        };

        var block = WorkflowVariableBlock.Render(bag).Block;

        block.Should().Contain("\nm: {\"\\u003Ck \\u00E9\\u003E\":\"v\"}\n");
    }

    /// <summary>
    /// An <see cref="IDictionary{TKey,TValue}"/> of string to int that implements neither the non-generic
    /// <see cref="System.Collections.IDictionary"/> nor a dictionary of <see cref="object"/>, so it enumerates only
    /// as key-value pairs.
    /// </summary>
    private sealed class CustomMap : IDictionary<string, int>
    {
        private readonly Dictionary<string, int> _inner = new(StringComparer.Ordinal);

        public int this[string key] { get => _inner[key]; set => _inner[key] = value; }

        public ICollection<string> Keys => _inner.Keys;

        public ICollection<int> Values => _inner.Values;

        public int Count => _inner.Count;

        public bool IsReadOnly => false;

        public void Add(string key, int value) => _inner.Add(key, value);

        public void Add(KeyValuePair<string, int> item) => _inner.Add(item.Key, item.Value);

        public void Clear() => _inner.Clear();

        public bool Contains(KeyValuePair<string, int> item) => _inner.Contains(item);

        public bool ContainsKey(string key) => _inner.ContainsKey(key);

        public void CopyTo(KeyValuePair<string, int>[] array, int arrayIndex) => ((ICollection<KeyValuePair<string, int>>)_inner).CopyTo(array, arrayIndex);

        public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => _inner.GetEnumerator();

        public bool Remove(string key) => _inner.Remove(key);

        public bool Remove(KeyValuePair<string, int> item) => ((ICollection<KeyValuePair<string, int>>)_inner).Remove(item);

        public bool TryGetValue(string key, out int value) => _inner.TryGetValue(key, out value);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
        ["bytes"] = new byte[] { 1, 2, 3 },
        ["l"] = new List<string> { "x", "y" },
        ["arr"] = new[] { 1, 2 },
        ["o"] = new List<object?> { "x", 1L, false, null },
        ["dict"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["z"] = 1, ["a"] = 2 },
        ["map"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = "v", ["inner"] = new List<object?> { 1.25 } },
        ["e"] = JsonDocument.Parse("""{"x":[1,2],"y":"é"}""").RootElement.Clone(),
        ["node"] = new JsonObject { ["k"] = "v", ["a"] = new JsonArray(1, "é") },
    };
}
