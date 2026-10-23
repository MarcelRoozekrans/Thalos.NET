using System.Text.Json.Serialization;

namespace Thalos.Tools;

/// <summary>
/// The source-generated metadata of a tool call's arguments, as <see cref="AuthorizingAIFunction"/> reads them for its
/// authorizer and its audit text. It is combined first, so Thalos owns the dictionary's metadata, and
/// <c>AIJsonUtilities.DefaultOptions</c>' own resolver follows it to supply the values' runtime types,
/// <see cref="System.Text.Json.JsonElement"/> among them.
/// </summary>
[JsonSerializable(typeof(IDictionary<string, object?>))]
internal sealed partial class ToolArgumentsJsonContext : JsonSerializerContext;
