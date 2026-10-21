using System.Text.Json;
using System.Text.Json.Serialization;

namespace Thalos.Mcp;

/// <summary>
/// Source-generated metadata for <see cref="McpConfigFile.Parse"/>, in the options the reflection-based reader used:
/// <see cref="JsonSerializerDefaults.Web"/>, so property names match case-insensitively, plus skipped comments and
/// allowed trailing commas. <see cref="TimeSpan"/> values such as <c>timeout</c> keep the serializer's built-in
/// converter, which reads the constant <c>"c"</c> format, <c>hh:mm:ss</c>.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(McpConfigFile.Root))]
internal sealed partial class McpConfigJsonContext : JsonSerializerContext;
