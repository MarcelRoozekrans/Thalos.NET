namespace Thalos;

/// <summary>Stable error codes returned on every Thalos boundary.</summary>
public enum AgentErrorCode
{
    Validation,
    AgentNotFound,
    SessionNotFound,
    SessionBusy,
    SessionClosed,
    Unauthorized,
    ToolDenied,
    ToolNotFound,
    Quarantined,
    ProviderError,
    StoreError,
    Cancelled,
}

/// <summary>Error value used with <c>Result&lt;T, AgentError&gt;</c>. Never throw this — return it.</summary>
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

    public override string ToString() => $"{Code}: {Message}";
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
