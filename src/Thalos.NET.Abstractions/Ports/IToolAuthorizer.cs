using System.Text.Json;
using ZeroAlloc.Authorization;

namespace Thalos;

/// <summary>
/// Outcome of a tool authorization check. <paramref name="Reason"/> is set only when denied.
/// Note that <see langword="default"/> is a denial (<c>Allowed == false</c>) — fail closed.
/// </summary>
public readonly record struct ToolAuthorizationDecision(bool Allowed, string? Reason)
{
    /// <summary>The call may proceed.</summary>
    public static ToolAuthorizationDecision Allow() => new(true, null);

    /// <summary>The call is refused; <paramref name="reason"/> is surfaced to the model and audit log.</summary>
    public static ToolAuthorizationDecision Deny(string reason) => new(false, reason);
}

/// <summary>Decides whether a caller may run a tool with the given arguments.</summary>
public interface IToolAuthorizer
{
    /// <summary>Decides whether <paramref name="caller"/> may run <paramref name="qualifiedToolName"/> with <paramref name="arguments"/>.</summary>
    ValueTask<ToolAuthorizationDecision> AuthorizeAsync(ISecurityContext caller, string qualifiedToolName, JsonElement arguments, CancellationToken ct);
}
