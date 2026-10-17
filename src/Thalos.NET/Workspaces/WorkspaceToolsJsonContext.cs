using System.Text.Json.Serialization;

namespace Thalos.Workspaces;

/// <summary>
/// The source-generated JSON metadata of <see cref="WorkspaceTools"/>' parameters and results, all strings, so the
/// tools bind and answer without reflection-based JSON.
/// </summary>
[JsonSerializable(typeof(string))]
internal sealed partial class WorkspaceToolsJsonContext : JsonSerializerContext;
