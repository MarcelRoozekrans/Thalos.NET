using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Thalos.Workflow.Orm;

/// <summary>
/// Every JSON read and write <see cref="OrmWorkflowStore"/> makes, gathered in one place so the shapes it stores and
/// enqueues can be pinned by tests: the <c>visits</c> column and the outbox dispatch payload in default options, the
/// <c>variables</c> column through <see cref="WorkflowVariableJson.Write"/>, and the <c>manifest</c>,
/// <c>started_by</c>, <c>last_resumed_by</c>, <c>usage</c> and <c>actor</c> columns in
/// <see cref="JsonSerializerDefaults.Web"/> options. Every call goes through source-generated metadata
/// (<see cref="WorkflowOrmJsonContext"/>, <see cref="WorkflowOrmWebJsonContext"/> and
/// <see cref="WorkflowDispatchMessage.JsonTypeInfo"/>), so none of it needs reflection.
/// </summary>
internal static class OrmWorkflowJson
{
    /// <summary>The <c>visits</c> column.</summary>
    public static string SerializeVisits(IReadOnlyDictionary<string, int> visits) => JsonSerializer.Serialize(visits, WorkflowOrmJsonContext.Default.IReadOnlyDictionaryStringInt32);

    /// <summary>The <c>visits</c> column, read back.</summary>
    public static Dictionary<string, int>? DeserializeVisits(string json) => JsonSerializer.Deserialize(json, WorkflowOrmJsonContext.Default.DictionaryStringInt32);

    /// <summary>The <c>variables</c> column of a run and of a run event.</summary>
    /// <exception cref="ArgumentException">A value is not JSON-shaped; see <see cref="WorkflowVariableJson.Validate"/>.</exception>
    public static string SerializeVariables(IReadOnlyDictionary<string, object?> variables)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WorkflowVariableJson.Write(writer, variables);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

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
    public static string SerializeManifest(RunManifest manifest) => JsonSerializer.Serialize(manifest, WorkflowOrmWebJsonContext.Default.RunManifest);

    /// <summary>The <c>manifest</c> column, read back.</summary>
    public static RunManifest? DeserializeManifest(string json) => JsonSerializer.Deserialize(json, WorkflowOrmWebJsonContext.Default.RunManifest);

    /// <summary>The <c>started_by</c> column and a run event's <c>actor</c>.</summary>
    public static string SerializePrincipal(RunPrincipal principal) => JsonSerializer.Serialize(principal, WorkflowOrmWebJsonContext.Default.RunPrincipal);

    /// <summary>The <c>started_by</c> column and a run event's <c>actor</c>, read back.</summary>
    public static RunPrincipal? DeserializePrincipal(string json) => JsonSerializer.Deserialize(json, WorkflowOrmWebJsonContext.Default.RunPrincipal);

    /// <summary>A run event's <c>usage</c>.</summary>
    public static string SerializeUsage(TurnUsage usage) => JsonSerializer.Serialize(usage, WorkflowOrmWebJsonContext.Default.TurnUsage);

    /// <summary>A run event's <c>usage</c>, read back.</summary>
    public static TurnUsage DeserializeUsage(string json) => JsonSerializer.Deserialize(json, WorkflowOrmWebJsonContext.Default.TurnUsage);

    /// <summary>The <c>last_resumed_by</c> column.</summary>
    public static string SerializeResumeEnvelope(LastResumeEnvelope envelope) => JsonSerializer.Serialize(envelope, WorkflowOrmWebJsonContext.Default.LastResumeEnvelope);

    /// <summary>The <c>last_resumed_by</c> column, read back.</summary>
    public static LastResumeEnvelope? DeserializeResumeEnvelope(string json) => JsonSerializer.Deserialize(json, WorkflowOrmWebJsonContext.Default.LastResumeEnvelope);

    /// <summary>The outbox payload enqueued under <see cref="WorkflowDispatch.TypeName"/>.</summary>
    public static byte[] SerializeDispatch(WorkflowDispatchMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, WorkflowDispatchMessage.JsonTypeInfo);
}

/// <summary>
/// <c>last_resumed_by</c>'s JSON shape: the principal and the signal it resumed, packed into one column so a
/// third dedicated column is not needed just to carry the signal alongside the approver.
/// </summary>
internal sealed record LastResumeEnvelope(RunPrincipal Principal, string Signal);
