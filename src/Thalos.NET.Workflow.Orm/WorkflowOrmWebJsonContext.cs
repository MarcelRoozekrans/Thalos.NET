using System.Text.Json;
using System.Text.Json.Serialization;

namespace Thalos.Workflow.Orm;

/// <summary>
/// Source-generated metadata for the <c>manifest</c>, <c>started_by</c>, <c>last_resumed_by</c>, <c>usage</c> and
/// <c>actor</c> columns, in <see cref="JsonSerializerDefaults.Web"/> options: camelCase property names, as the
/// reflection-based serializer wrote them. <see cref="AgentId"/> carries its own generated converter attribute, which
/// the generator honours, so it still round-trips through its 26-character ULID form.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RunManifest))]
[JsonSerializable(typeof(RunPrincipal))]
[JsonSerializable(typeof(TurnUsage))]
[JsonSerializable(typeof(LastResumeEnvelope))]
internal sealed partial class WorkflowOrmWebJsonContext : JsonSerializerContext;
