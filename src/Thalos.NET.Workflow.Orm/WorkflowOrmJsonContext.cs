using System.Text.Json.Serialization;

namespace Thalos.Workflow.Orm;

/// <summary>
/// Source-generated metadata for the <c>visits</c> column, in default options, as the reflection-based serializer
/// wrote it: written from the store's read-only view, read back as a dictionary. The <c>variables</c> column needs
/// none: <see cref="WorkflowVariableJson.Write"/> writes it.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, int>))]
internal sealed partial class WorkflowOrmJsonContext : JsonSerializerContext;
