using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Thalos.Anthropic;

/// <summary>
/// The tool input-schema transform of the Anthropic SDK 13.3.0 chat client (<c>AnthropicClientExtensions</c>,
/// <c>JsonSchemaTransformCache</c>), reproduced with its settings: object schemas get <c>additionalProperties: false</c>,
/// <c>oneOf</c> becomes <c>anyOf</c>, keywords outside the per-type whitelist are removed, and what was removed is
/// appended to the schema's description so the model may still follow it.
/// </summary>
/// <remarks>
/// The whitelist matches the SDK's, which follows the TypeScript and Python SDKs and also keeps <c>enum</c> and
/// <c>const</c>. A union type such as <c>["string","null"]</c> keeps the keywords of every member type. The transform
/// works on <see cref="JsonNode"/> trees only; no reflection-based serialization is involved.
/// </remarks>
internal static class AnthropicToolSchema
{
    /// <summary>Writes removed values the way JavaScript's <c>JSON.stringify</c> does, without escaping <c>+</c> or <c>'</c>.</summary>
    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The string formats Anthropic supports; any other <c>format</c> is removed.</summary>
    private static readonly HashSet<string> SupportedStringFormats = new(StringComparer.Ordinal)
    {
        "date-time", "time", "date", "duration", "email", "hostname", "uri", "ipv4", "ipv6", "uuid",
    };

    private static readonly HashSet<string> BaseProperties = new(StringComparer.Ordinal)
    {
        "type", "description", "title", "$ref", "$defs", "anyOf", "allOf", "enum", "const",
    };

    private static readonly HashSet<string> ObjectProperties = new(BaseProperties, StringComparer.Ordinal)
    {
        "properties", "required", "additionalProperties",
    };

    private static readonly HashSet<string> StringProperties = new(BaseProperties, StringComparer.Ordinal) { "format" };

    private static readonly HashSet<string> ArrayProperties = new(BaseProperties, StringComparer.Ordinal) { "items", "minItems" };

    private static readonly AIJsonSchemaTransformCache Cache = new(new AIJsonSchemaTransformOptions
    {
        DisallowAdditionalProperties = true,
        TransformSchemaNode = static (_, node) => Transform(node),
    });

    /// <summary>The transformed input schema of <paramref name="function"/>.</summary>
    public static JsonElement For(AIFunctionDeclaration function) => Cache.GetOrCreateTransformedSchema(function);

    private static HashSet<string> SupportedProperties(string? type) => type switch
    {
        "object" => ObjectProperties,
        "string" => StringProperties,
        "array" => ArrayProperties,
        _ => BaseProperties,
    };

    private static JsonNode Transform(JsonNode node)
    {
        if (node is not JsonObject schema)
        {
            return node;
        }

        // The Anthropic API documents anyOf, not oneOf, for unions.
        if (schema.TryGetPropertyValue("oneOf", out var oneOf) && oneOf is not null)
        {
            schema.Remove("oneOf");
            schema["anyOf"] = oneOf;
        }

        var (type, unionTypes) = Types(schema);
        bool HasType(string candidate) => unionTypes is null ? string.Equals(type, candidate, StringComparison.Ordinal) : unionTypes.Contains(candidate);

        List<KeyValuePair<string, string>>? removed = null;

        if (HasType("string") && schema.TryGetPropertyValue("format", out var formatNode)
            && !(formatNode is JsonValue formatValue && formatValue.TryGetValue(out string? format) && SupportedStringFormats.Contains(format)))
        {
            schema.Remove("format");
            (removed ??= []).Add(new("format", Serialize(formatNode)));
        }

        if (HasType("array") && schema.TryGetPropertyValue("minItems", out var minItemsNode)
            && !(minItemsNode is JsonValue minItemsValue && minItemsValue.TryGetValue(out int minItems) && minItems is 0 or 1))
        {
            schema.Remove("minItems");
            (removed ??= []).Add(new("minItems", Serialize(minItemsNode)));
        }

        var supported = Supported(type, unionTypes);
        // A copy, since properties are removed while walking them.
        var properties = schema.ToArray();
        foreach (var property in properties)
        {
            if (!supported.Contains(property.Key))
            {
                schema.Remove(property.Key);
                (removed ??= []).Add(new(property.Key, Serialize(property.Value)));
            }
        }

        if (removed is { Count: > 0 })
        {
            var existing = schema.TryGetPropertyValue("description", out var description) ? description?.GetValue<string>() : null;
            var constraints = "{" + string.Join(", ", removed.Select(static c => $"{c.Key}: {c.Value}")) + "}";
            schema["description"] = existing is not null ? $"{existing}\n\n{constraints}" : constraints;
        }

        return schema;
    }

    /// <summary>The schema's type, or the member types of a union type array.</summary>
    private static (string? Type, HashSet<string>? UnionTypes) Types(JsonObject schema)
    {
        schema.TryGetPropertyValue("type", out var typeNode);
        if (typeNode is JsonValue typeValue && typeValue.TryGetValue(out string? typeName))
        {
            return (typeName, null);
        }

        if (typeNode is not JsonArray typeArray)
        {
            return (null, null);
        }

        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in typeArray)
        {
            if (member is JsonValue memberValue && memberValue.TryGetValue(out string? memberName))
            {
                members.Add(memberName);
            }
        }

        return (null, members);
    }

    private static HashSet<string> Supported(string? type, HashSet<string>? unionTypes)
    {
        if (unionTypes is null)
        {
            return SupportedProperties(type);
        }

        var supported = new HashSet<string>(BaseProperties, StringComparer.Ordinal);
        foreach (var member in unionTypes)
        {
            supported.UnionWith(SupportedProperties(member));
        }

        return supported;
    }

    private static string Serialize(JsonNode? node) => node?.ToJsonString(RelaxedJson) ?? "null";
}
