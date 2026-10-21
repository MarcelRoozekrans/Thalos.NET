using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Thalos.Workflow;

/// <summary>
/// The JSON-shaped contract every <see cref="WorkflowRun.Variables"/> value keeps: a <see cref="string"/>, a number
/// (<see cref="byte"/> through <see cref="ulong"/>, <see cref="float"/>, <see cref="double"/>, <see cref="decimal"/>),
/// a <see cref="bool"/>, <see langword="null"/>, a <see cref="JsonElement"/>, a <see cref="JsonNode"/>, or a list or
/// a string-keyed dictionary of those. A <see cref="float"/> or <see cref="double"/> must be finite, because JSON has
/// no NaN or infinity. A <c>byte[]</c> counts as a string: it is written as Base64, as the reflection-based serializer
/// wrote it, and reads back as that string. Every entry point a bag comes in through checks it with <see cref="Validate"/>,
/// and the store and the prompt block write it with <see cref="Write"/>, which needs no reflection and no
/// <see cref="JsonSerializer"/> call, so it works the same under trimming and Native AOT as on the JIT.
/// </summary>
/// <remarks>
/// <para>
/// The bytes are the ones the reflection-based serializer wrote with default options: the default encoder, entry
/// order kept, every number through its own <see cref="Utf8JsonWriter.WriteNumberValue(double)"/> overload, so a
/// <see cref="double"/> <c>2.0</c> is still written as <c>2</c> and reads back as a <see cref="long"/>.
/// </para>
/// <para>
/// A dictionary is recognised as <see cref="IReadOnlyDictionary{TKey,TValue}"/> or <see cref="IDictionary{TKey,TValue}"/>
/// of <see cref="string"/> to <see cref="object"/>, or as the non-generic <see cref="IDictionary"/> with string keys,
/// which <see cref="Dictionary{TKey,TValue}"/> and the other framework dictionaries implement for any value type. A
/// custom <c>IDictionary&lt;string, T&gt;</c> that implements none of those enumerates as key-value pairs; it is
/// refused, naming the dictionary's own type, rather than written in a shape nobody asked for.
/// </para>
/// <para>
/// A list or other sequence must be re-enumerable, with the same members each time: <see cref="Validate"/> walks it
/// at the entry point, and <see cref="Write"/> walks it twice more, once to check and once to write. A one-shot
/// iterator is empty on the second walk.
/// </para>
/// <para>
/// Nothing here converts the values a run holds: the in-memory bag and the bag read back from a store keep their CLR
/// values. This class only decides what is allowed in and how it is written out.
/// </para>
/// </remarks>
public static class WorkflowVariableJson
{
    /// <summary>
    /// The deepest a value may nest, counting the bag itself as one level, so a variable may hold 63 nested lists or
    /// dictionaries and not 64. That is the depth <see cref="JsonDocument"/> parses by default, so a bag this accepts
    /// still reads back from a store. It is also where the reflection-based serializer stopped: it wrote a bag holding
    /// 63 nested lists and threw a <see cref="JsonException"/> for 64, which is now an <see cref="ArgumentException"/>
    /// at the entry point instead of a failure in the store. The limit also stops a list that contains itself, which
    /// would otherwise overflow the stack.
    /// </summary>
    private const int MaxDepth = 64;

    private const string Allowed = "Allowed: string, number, bool, null, JsonElement, JsonNode, and lists or dictionaries of those.";

    /// <summary>
    /// Throws <see cref="ArgumentException"/> for <paramref name="paramName"/> when any value in
    /// <paramref name="variables"/>, at any depth, is not JSON-shaped. The message names the key, or for a nested
    /// value its path such as <c>files[2]</c> or <c>meta.owner</c>, and the value's full type name. A
    /// <see langword="null"/> bag is allowed.
    /// </summary>
    public static void Validate(IReadOnlyDictionary<string, object?>? variables, string paramName)
    {
        if (variables is null)
        {
            return;
        }

        foreach (var (key, value) in variables)
        {
            var refusal = Walk(null, value, depth: 2);
            if (refusal is not null)
            {
                throw refusal.ToException(key, paramName);
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="writer"/> as JSON. Throws <see cref="ArgumentException"/>,
    /// with the message <see cref="Validate"/> gives and nothing written, when the value is not JSON-shaped; a bare
    /// value's path starts at the root, as <c>$</c>, <c>[2]</c> or <c>files[2]</c>.
    /// </summary>
    public static void Write(Utf8JsonWriter writer, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var refusal = Walk(null, value, depth: 1);
        if (refusal is not null)
        {
            throw refusal.ToException(root: null, nameof(value));
        }

        Walk(writer, value, depth: 1);
    }

    /// <summary>
    /// <paramref name="value"/> as a <see cref="JsonElement"/>, written by <see cref="Write"/>. For rendering and
    /// comparison only: a run's bag never holds the result.
    /// </summary>
    public static JsonElement ToElement(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Checks <paramref name="value"/> when <paramref name="writer"/> is <see langword="null"/>, and writes it
    /// otherwise. Returns the refusal for the first value that is not JSON-shaped, its path collected on the way out,
    /// so a value that is allowed costs no path allocation.
    /// </summary>
    private static Refusal? Walk(Utf8JsonWriter? writer, object? value, int depth)
    {
        if (value is null)
        {
            writer?.WriteNullValue();
            return null;
        }

        if ((value is double d && !double.IsFinite(d)) || (value is float f && !float.IsFinite(f)))
        {
            return Refusal.NonFinite(value);
        }

        if (TryWriteNumber(writer, value) || TryWriteLeaf(writer, value))
        {
            return null;
        }

        if (depth > MaxDepth)
        {
            return Refusal.TooDeep();
        }

        return value switch
        {
            IReadOnlyDictionary<string, object?> map => WalkEntries(writer, map, depth),
            IDictionary<string, object?> map => WalkEntries(writer, map, depth),
            IDictionary map => WalkDictionary(writer, map, depth),
            IEnumerable sequence => WalkSequence(writer, sequence, depth),
            _ => Refusal.ForType(value.GetType()),
        };
    }

    /// <summary>A string, a bool, a <see cref="JsonElement"/> or a <see cref="JsonNode"/>, written when there is a writer.</summary>
    private static bool TryWriteLeaf(Utf8JsonWriter? writer, object value)
    {
        switch (value)
        {
            case string s:
                writer?.WriteStringValue(s);
                return true;
            case bool b:
                writer?.WriteBooleanValue(b);
                return true;
            case byte[] bytes:
                writer?.WriteBase64StringValue(bytes);
                return true;
            case JsonElement element:
                if (writer is not null)
                {
                    element.WriteTo(writer);
                }

                return true;
            case JsonNode node:
                if (writer is not null)
                {
                    node.WriteTo(writer);
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>A number, written through its own overload, or the nearest one for the types the writer has none for.</summary>
    private static bool TryWriteNumber(Utf8JsonWriter? writer, object value)
    {
        switch (value)
        {
            case byte n:
                writer?.WriteNumberValue(n);
                return true;
            case sbyte n:
                writer?.WriteNumberValue(n);
                return true;
            case short n:
                writer?.WriteNumberValue(n);
                return true;
            case ushort n:
                writer?.WriteNumberValue(n);
                return true;
            case int n:
                writer?.WriteNumberValue(n);
                return true;
            case uint n:
                writer?.WriteNumberValue(n);
                return true;
            case long n:
                writer?.WriteNumberValue(n);
                return true;
            case ulong n:
                writer?.WriteNumberValue(n);
                return true;
            case float n:
                writer?.WriteNumberValue(n);
                return true;
            case double n:
                writer?.WriteNumberValue(n);
                return true;
            case decimal n:
                writer?.WriteNumberValue(n);
                return true;
            default:
                return false;
        }
    }

    private static Refusal? WalkEntries(Utf8JsonWriter? writer, IEnumerable<KeyValuePair<string, object?>> entries, int depth)
    {
        writer?.WriteStartObject();
        foreach (var (key, member) in entries)
        {
            writer?.WritePropertyName(key);
            if (Walk(writer, member, depth + 1) is { } refusal)
            {
                return refusal.Under(key);
            }
        }

        writer?.WriteEndObject();
        return null;
    }

    private static Refusal? WalkDictionary(Utf8JsonWriter? writer, IDictionary map, int depth)
    {
        writer?.WriteStartObject();
        foreach (DictionaryEntry entry in map)
        {
            if (entry.Key is not string key)
            {
                return Refusal.ForType(map.GetType());
            }

            writer?.WritePropertyName(key);
            if (Walk(writer, entry.Value, depth + 1) is { } refusal)
            {
                return refusal.Under(key);
            }
        }

        writer?.WriteEndObject();
        return null;
    }

    private static Refusal? WalkSequence(Utf8JsonWriter? writer, IEnumerable sequence, int depth)
    {
        writer?.WriteStartArray();
        var index = 0;
        foreach (var item in sequence)
        {
            if (Walk(writer, item, depth + 1) is { } refusal)
            {
                // A custom IDictionary<string, T> enumerates as KeyValuePair members: name the dictionary, at its own
                // path, rather than a pair inside it.
                return refusal.IsPair(item) ? Refusal.ForType(sequence.GetType()) : refusal.Under(index);
            }

            index++;
        }

        writer?.WriteEndArray();
        return null;
    }

    /// <summary>Why a value was refused, and its path below the value the walk started at, innermost segment first.</summary>
    private sealed class Refusal
    {
        private readonly Type? _type;
        private object? _value;
        private readonly List<object> _segments = [];

        private Refusal(Type? type) => _type = type;

        public static Refusal ForType(Type type) => new(type);

        public static Refusal TooDeep() => new(type: null);

        public static Refusal NonFinite(object value) => new(type: null) { _value = value };

        /// <summary>Whether this refuses <paramref name="item"/> itself, a <see cref="KeyValuePair{TKey,TValue}"/>.</summary>
        public bool IsPair(object? item) =>
            _segments.Count == 0 && _type is { IsGenericType: true } type && item is not null
            && type == item.GetType() && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);

        public Refusal Under(string key)
        {
            _segments.Add(key);
            return this;
        }

        public Refusal Under(int index)
        {
            _segments.Add(index);
            return this;
        }

        /// <summary>The exception, its path starting at <paramref name="root"/>, a variable's key, or at <c>$</c> when there is none.</summary>
        public ArgumentException ToException(string? root, string paramName)
        {
            var path = new StringBuilder(root);
            for (var i = _segments.Count - 1; i >= 0; i--)
            {
                _ = _segments[i] switch
                {
                    int index => path.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']'),
                    var key when path.Length == 0 => path.Append(key),
                    var key => path.Append('.').Append(key),
                };
            }

            var where = path.Length == 0 ? "$" : path.ToString();
            var message = (_type, _value) switch
            {
                (null, null) => $"Workflow variable '{where}' nests deeper than {MaxDepth} levels.",
                (null, IFormattable number) => $"Workflow variable '{where}' is {number.ToString(null, CultureInfo.InvariantCulture)}, which is not a JSON number. A float or double must be finite.",
                _ => $"Workflow variable '{where}' has type '{_type?.FullName}', which is not JSON-shaped. {Allowed}",
            };
            return new ArgumentException(message, paramName);
        }
    }
}
