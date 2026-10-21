using System.Text.Json.Serialization;

namespace Thalos.Workflow;

/// <summary>
/// Source-generated metadata for the <see cref="WorkflowDispatchMessage"/> outbox payload, in default options, so the
/// payload is written with the same PascalCase names and bytes the reflection-based serializer wrote. Reached through
/// <see cref="WorkflowDispatchMessage.JsonTypeInfo"/>, which a store writes the payload with and a dispatcher reads
/// it back with.
/// </summary>
[JsonSerializable(typeof(WorkflowDispatchMessage))]
internal sealed partial class WorkflowJsonContext : JsonSerializerContext;
