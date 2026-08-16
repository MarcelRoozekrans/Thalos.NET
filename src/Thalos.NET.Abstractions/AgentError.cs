namespace Thalos;

/// <summary>Stable error codes returned on every Thalos boundary. Suggested HTTP mapping in each member's docs.</summary>
public enum AgentErrorCode
{
    /// <summary>The request was malformed (empty text, invalid definition…). HTTP 400.</summary>
    Validation,

    /// <summary>No agent definition is registered under the given id. HTTP 404.</summary>
    AgentNotFound,

    /// <summary>The session id is unknown to the store. HTTP 404.</summary>
    SessionNotFound,

    /// <summary>The session is already running a turn; retry after it completes. HTTP 409.</summary>
    SessionBusy,

    /// <summary>The session was closed and accepts no more turns. HTTP 409.</summary>
    SessionClosed,

    /// <summary>The caller may not access this agent or session. HTTP 403.</summary>
    Unauthorized,

    /// <summary>The tool authorizer refused a tool call the model requested; <see cref="AgentError.Detail"/> carries the reason. HTTP 422.</summary>
    ToolDenied,

    /// <summary>The model requested a tool that is not in the agent's tool set. HTTP 404.</summary>
    ToolNotFound,

    /// <summary>A safety layer (e.g. AI.Sentinel) quarantined the turn. HTTP 422.</summary>
    Quarantined,

    /// <summary>The model provider failed (network, auth, rate limit, malformed response). HTTP 502.</summary>
    ProviderError,

    /// <summary>The session store failed. HTTP 502.</summary>
    StoreError,

    /// <summary>The caller cancelled the operation. HTTP 499.</summary>
    Cancelled,
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
    public static AgentError Validation(string message) => new(AgentErrorCode.Validation, message);
    public static AgentError AgentNotFound(AgentId id) => new(AgentErrorCode.AgentNotFound, $"Agent '{id}' is not registered.");
    public static AgentError SessionNotFound(SessionId id) => new(AgentErrorCode.SessionNotFound, $"Session '{id}' was not found.");
    public static AgentError SessionBusy(SessionId id) => new(AgentErrorCode.SessionBusy, $"Session '{id}' is already running a turn.");
    public static AgentError SessionClosed(SessionId id) => new(AgentErrorCode.SessionClosed, $"Session '{id}' is closed.");
    public static AgentError Unauthorized(string reason) => new(AgentErrorCode.Unauthorized, reason);
    public static AgentError ToolDenied(string toolName, string reason) => new(AgentErrorCode.ToolDenied, $"Tool '{toolName}' was denied.", reason);
    public static AgentError ToolNotFound(string toolName) => new(AgentErrorCode.ToolNotFound, $"Tool '{toolName}' is not available.");
    public static AgentError Quarantined(string message, string? detail = null) => new(AgentErrorCode.Quarantined, message, detail);
    public static AgentError ProviderError(string message, string? detail = null) => new(AgentErrorCode.ProviderError, message, detail);
    public static AgentError StoreError(string message, string? detail = null) => new(AgentErrorCode.StoreError, message, detail);
    public static AgentError Cancelled() => new(AgentErrorCode.Cancelled, "The operation was cancelled.");

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
    public AgentError Error { get; } = error;
}
