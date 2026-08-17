namespace Thalos;

/// <summary>Stable error codes returned on every Thalos boundary. Suggested HTTP mapping in each member's docs.</summary>
public enum AgentErrorCode
{
    /// <summary>The request was malformed (empty text, invalid definition…). HTTP 400.</summary>
    Validation,

    /// <summary>No agent definition is registered under the given id. HTTP 404.</summary>
    AgentNotFound,

    /// <summary>
    /// The session id is unknown to the store — or exists but belongs to another owner and the caller lacks the admin role
    /// (the runtime deliberately answers 404, not 403, so session ids cannot be probed). HTTP 404.
    /// </summary>
    SessionNotFound,

    /// <summary>The session is already running a turn; retry after it completes. HTTP 409.</summary>
    SessionBusy,

    /// <summary>The session was closed and accepts no more turns. HTTP 409.</summary>
    SessionClosed,

    /// <summary>
    /// The caller may not perform this operation. HTTP 403. The 0.1 runtime does not produce it: a foreign session answers
    /// <see cref="SessionNotFound"/> (404) instead; the code is available for host/channel-level checks.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// Reserved (not produced in 0.1): the tool authorizer refused a tool call. In 0.1 a denial is returned to the model as a
    /// <c>"Tool call denied: …"</c> tool result and surfaces as <c>ToolCallFinishedEvent</c> / <c>ToolCallDeniedNotification</c>; the turn continues. HTTP 422.
    /// </summary>
    ToolDenied,

    /// <summary>Reserved (not produced in 0.1): the model requested a tool outside the agent's tool set; MAF returns an error result to the model instead. HTTP 404.</summary>
    ToolNotFound,

    /// <summary>A safety layer (e.g. AI.Sentinel) quarantined the turn. HTTP 422.</summary>
    Quarantined,

    /// <summary>The model provider failed (network, auth, rate limit, malformed response). HTTP 502.</summary>
    ProviderError,

    /// <summary>The session store failed. HTTP 502.</summary>
    StoreError,

    /// <summary>The caller cancelled the operation. HTTP 499.</summary>
    Cancelled,

    /// <summary>No memory record exists under the given id (or it is not visible to the caller). HTTP 404.</summary>
    MemoryNotFound,

    /// <summary>The memory store (records) failed. HTTP 502.</summary>
    MemoryStoreFailed,

    /// <summary>The memory index (vectors / embedding generator) is unreachable; records may still be stored with <c>IndexPending</c>. HTTP 503.</summary>
    MemoryIndexUnavailable,

    /// <summary>The memory index rejected an operation (schema, dimension, malformed data). HTTP 502.</summary>
    MemoryIndexFailed,

    /// <summary>A memory request violated the limits (empty text, > 4000 chars, > 10 tags, importance outside 0..1, bad kind). HTTP 400.</summary>
    MemoryValidationFailed,

    /// <summary>The memory belongs to another owner (forget/hard-delete scope check). HTTP 403.</summary>
    MemoryForbidden,
}

/// <summary>
/// Error value used with <c>Result&lt;T, AgentError&gt;</c>. Never throw this — return it.
/// Record equality includes <see cref="Detail"/>: two errors with the same code and message but different detail are not equal.
/// </summary>
/// <param name="Code">Stable error code; see <see cref="AgentErrorCode"/> for the suggested HTTP mapping.</param>
/// <param name="Message">Human-readable, safe to show to the caller.</param>
/// <param name="Detail">
/// Diagnostic addendum (exception type name, detector id, tool source name); contains no untrusted content by policy —
/// raw provider/tool exception messages are logged, never copied here — so it is safe to forward to clients.
/// </param>
public readonly record struct AgentError(AgentErrorCode Code, string Message, string? Detail = null)
{
    /// <summary><see cref="AgentErrorCode.Validation"/> with the given message (e.g. <c>"Text is required."</c>).</summary>
    public static AgentError Validation(string message) => new(AgentErrorCode.Validation, message);

    /// <summary><see cref="AgentErrorCode.AgentNotFound"/> for <paramref name="id"/>.</summary>
    public static AgentError AgentNotFound(AgentId id) => new(AgentErrorCode.AgentNotFound, $"Agent '{id}' is not registered.");

    /// <summary><see cref="AgentErrorCode.SessionNotFound"/> for <paramref name="id"/> (also used for sessions the caller may not see).</summary>
    public static AgentError SessionNotFound(SessionId id) => new(AgentErrorCode.SessionNotFound, $"Session '{id}' was not found.");

    /// <summary><see cref="AgentErrorCode.SessionBusy"/>: <paramref name="id"/> is already running a turn.</summary>
    public static AgentError SessionBusy(SessionId id) => new(AgentErrorCode.SessionBusy, $"Session '{id}' is already running a turn.");

    /// <summary><see cref="AgentErrorCode.SessionClosed"/>: <paramref name="id"/> accepts no more turns.</summary>
    public static AgentError SessionClosed(SessionId id) => new(AgentErrorCode.SessionClosed, $"Session '{id}' is closed.");

    /// <summary><see cref="AgentErrorCode.Unauthorized"/> with <paramref name="reason"/> as the message.</summary>
    public static AgentError Unauthorized(string reason) => new(AgentErrorCode.Unauthorized, reason);

    /// <summary>Reserved: <see cref="AgentErrorCode.ToolDenied"/> for <paramref name="toolName"/>, <paramref name="reason"/> in <see cref="Detail"/>.</summary>
    public static AgentError ToolDenied(string toolName, string reason) => new(AgentErrorCode.ToolDenied, $"Tool '{toolName}' was denied.", reason);

    /// <summary>Reserved: <see cref="AgentErrorCode.ToolNotFound"/> for <paramref name="toolName"/>.</summary>
    public static AgentError ToolNotFound(string toolName) => new(AgentErrorCode.ToolNotFound, $"Tool '{toolName}' is not available.");

    /// <summary><see cref="AgentErrorCode.Quarantined"/>; <paramref name="detail"/> is <c>"&lt;Severity&gt;: &lt;DetectorId&gt;"</c> when raised by Thalos.NET.Sentinel.</summary>
    public static AgentError Quarantined(string message, string? detail = null) => new(AgentErrorCode.Quarantined, message, detail);

    /// <summary><see cref="AgentErrorCode.ProviderError"/>; <paramref name="detail"/> is a diagnostic such as the exception type name or the failing tool source.</summary>
    public static AgentError ProviderError(string message, string? detail = null) => new(AgentErrorCode.ProviderError, message, detail);

    /// <summary><see cref="AgentErrorCode.StoreError"/>; <paramref name="detail"/> is a diagnostic such as the exception type name.</summary>
    public static AgentError StoreError(string message, string? detail = null) => new(AgentErrorCode.StoreError, message, detail);

    /// <summary><see cref="AgentErrorCode.Cancelled"/>: the caller's token was cancelled after the session was claimed.</summary>
    public static AgentError Cancelled() => new(AgentErrorCode.Cancelled, "The operation was cancelled.");

    /// <summary><see cref="AgentErrorCode.MemoryNotFound"/> for <paramref name="id"/>.</summary>
    public static AgentError MemoryNotFound(MemoryId id) => new(AgentErrorCode.MemoryNotFound, $"Memory '{id}' was not found.");

    /// <summary><see cref="AgentErrorCode.MemoryStoreFailed"/>; <paramref name="detail"/> is a diagnostic such as the exception type name.</summary>
    public static AgentError MemoryStoreFailed(string message, string? detail = null) => new(AgentErrorCode.MemoryStoreFailed, message, detail);

    /// <summary><see cref="AgentErrorCode.MemoryIndexUnavailable"/>; <paramref name="detail"/> is a diagnostic such as the exception type name.</summary>
    public static AgentError MemoryIndexUnavailable(string message, string? detail = null) => new(AgentErrorCode.MemoryIndexUnavailable, message, detail);

    /// <summary><see cref="AgentErrorCode.MemoryIndexFailed"/>; <paramref name="detail"/> is a diagnostic such as a SQL state.</summary>
    public static AgentError MemoryIndexFailed(string message, string? detail = null) => new(AgentErrorCode.MemoryIndexFailed, message, detail);

    /// <summary><see cref="AgentErrorCode.MemoryValidationFailed"/> with the given message.</summary>
    public static AgentError MemoryValidationFailed(string message) => new(AgentErrorCode.MemoryValidationFailed, message);

    /// <summary><see cref="AgentErrorCode.MemoryForbidden"/>: <paramref name="id"/> belongs to another owner.</summary>
    public static AgentError MemoryForbidden(MemoryId id) => new(AgentErrorCode.MemoryForbidden, $"Memory '{id}' belongs to another owner.");

    /// <summary><c>"{Code}: {Message}"</c>, with <c>" — {Detail}"</c> appended when <see cref="Detail"/> is set.</summary>
    public override string ToString() => Detail is null ? $"{Code}: {Message}" : $"{Code}: {Message} — {Detail}";
}

/// <summary>
/// Thrown *only* from inside code that has to cross an exception-based boundary (MAF, provider SDKs)
/// so the runtime can turn it back into an <see cref="AgentError"/>. Application code never sees it.
/// </summary>
public sealed class AgentTurnException(AgentError error, Exception? inner = null)
    : Exception(error.ToString(), inner)
{
    /// <summary>The error the runtime reports for the turn.</summary>
    public AgentError Error { get; } = error;
}
