using System.Text.Json;

namespace Thalos.Workflow.Orm;

/// <summary>
/// Every JSON read and write <see cref="OrmWorkflowStore"/> makes, gathered in one place so the shapes it stores and
/// enqueues can be pinned by tests: the <c>visits</c> and <c>variables</c> columns and the outbox dispatch payload
/// in default options, and the <c>manifest</c>, <c>started_by</c>, <c>last_resumed_by</c>, <c>usage</c> and
/// <c>actor</c> columns in <see cref="JsonSerializerDefaults.Web"/> options.
/// </summary>
internal static class OrmWorkflowJson
{
    /// <summary>
    /// <see cref="RunManifest"/>'s JSON shape on the wire and in the <c>manifest</c> column,
    /// <see cref="RunPrincipal"/>'s in the <c>started_by</c> column, and <see cref="LastResumeEnvelope"/>'s in
    /// <c>last_resumed_by</c>, and <see cref="TurnUsage"/>'s in <c>workflow_run_event.usage</c>: web defaults (camelCase property names), matching <c>Thalos.Mcp.McpConfigFile</c>'s
    /// convention for the same <see cref="JsonSerializerDefaults.Web"/> preset elsewhere in this repository.
    /// <see cref="AgentId"/> carries its own generated
    /// <see cref="System.Text.Json.Serialization.JsonConverterAttribute"/>, so it round-trips through its
    /// 26-character ULID form without this options instance needing to say anything about it.
    /// </summary>
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The <c>visits</c> column.</summary>
    public static string SerializeVisits(IReadOnlyDictionary<string, int> visits) => JsonSerializer.Serialize(visits);

    /// <summary>The <c>visits</c> column, read back.</summary>
    public static Dictionary<string, int>? DeserializeVisits(string json) => JsonSerializer.Deserialize<Dictionary<string, int>>(json);

    /// <summary>The <c>variables</c> column of a run and of a run event.</summary>
    public static string SerializeVariables(IReadOnlyDictionary<string, object?> variables) => JsonSerializer.Serialize(variables);

    /// <summary>
    /// Deserializing straight to <c>Dictionary&lt;string, object?&gt;</c> leaves every value a boxed
    /// <see cref="JsonElement"/>, not the plain CLR value a consumer reading <see cref="WorkflowRun.Variables"/>
    /// would expect — comparing a boxed <see cref="JsonElement"/> string against a bare <see cref="string"/>
    /// never succeeds. Parses to a <see cref="JsonElement"/> instead and hands it to
    /// <c>WorkflowVariableBlock.ReadObject</c>, so the bag holds ordinary strings, numbers, booleans, nulls,
    /// dictionaries and lists.
    /// </summary>
    /// <remarks>
    /// That method, and not a copy of it here, on purpose: it is also what <c>WorkflowNodeDispatcher</c> unwraps
    /// a reported variables object with on the way <em>in</em>. Two copies would let a value come back out of
    /// this column as a different CLR type than it went in as — which is precisely what happened while the
    /// unwrapping did live in two places.
    /// </remarks>
    public static Dictionary<string, object?> DeserializeVariables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? WorkflowVariableBlock.ReadObject(document.RootElement)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>The <c>manifest</c> column.</summary>
    public static string SerializeManifest(RunManifest manifest) => JsonSerializer.Serialize(manifest, ManifestJsonOptions);

    /// <summary>The <c>manifest</c> column, read back.</summary>
    public static RunManifest? DeserializeManifest(string json) => JsonSerializer.Deserialize<RunManifest>(json, ManifestJsonOptions);

    /// <summary>The <c>started_by</c> column and a run event's <c>actor</c>.</summary>
    public static string SerializePrincipal(RunPrincipal principal) => JsonSerializer.Serialize(principal, ManifestJsonOptions);

    /// <summary>The <c>started_by</c> column and a run event's <c>actor</c>, read back.</summary>
    public static RunPrincipal? DeserializePrincipal(string json) => JsonSerializer.Deserialize<RunPrincipal>(json, ManifestJsonOptions);

    /// <summary>A run event's <c>usage</c>.</summary>
    public static string SerializeUsage(TurnUsage usage) => JsonSerializer.Serialize(usage, ManifestJsonOptions);

    /// <summary>A run event's <c>usage</c>, read back.</summary>
    public static TurnUsage DeserializeUsage(string json) => JsonSerializer.Deserialize<TurnUsage>(json, ManifestJsonOptions);

    /// <summary>The <c>last_resumed_by</c> column.</summary>
    public static string SerializeResumeEnvelope(LastResumeEnvelope envelope) => JsonSerializer.Serialize(envelope, ManifestJsonOptions);

    /// <summary>The <c>last_resumed_by</c> column, read back.</summary>
    public static LastResumeEnvelope? DeserializeResumeEnvelope(string json) => JsonSerializer.Deserialize<LastResumeEnvelope>(json, ManifestJsonOptions);

    /// <summary>The outbox payload enqueued under <see cref="WorkflowDispatch.TypeName"/>.</summary>
    public static byte[] SerializeDispatch(WorkflowDispatchMessage message) => JsonSerializer.SerializeToUtf8Bytes(message);
}

/// <summary>
/// <c>last_resumed_by</c>'s JSON shape: the principal and the signal it resumed, packed into one column so a
/// third dedicated column is not needed just to carry the signal alongside the approver.
/// </summary>
internal sealed record LastResumeEnvelope(RunPrincipal Principal, string Signal);
