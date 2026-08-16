using System.Text.Json;
using System.Text.Json.Serialization;

namespace Thalos.Mcp;

/// <summary>Reads Claude Code-compatible <c>.mcp.json</c> (<c>{ "mcpServers": { name: {...} } }</c>).</summary>
public static class McpConfigFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Parses the JSON text of a <c>.mcp.json</c> file.</summary>
    /// <exception cref="JsonException">The document is empty or malformed.</exception>
    public static IReadOnlyDictionary<string, McpServerDefinition> Parse(string json)
    {
        var root = JsonSerializer.Deserialize<Root>(json, Options) ?? throw new JsonException("Empty .mcp.json");
        return root.McpServers ?? new Dictionary<string, McpServerDefinition>(StringComparer.Ordinal);
    }

    /// <summary>Reads and parses the <c>.mcp.json</c> file at <paramref name="path"/>.</summary>
    public static IReadOnlyDictionary<string, McpServerDefinition> Load(string path) => Parse(File.ReadAllText(path));

    private sealed class Root
    {
        [JsonPropertyName("mcpServers")]
        public Dictionary<string, McpServerDefinition>? McpServers { get; set; }
    }
}
