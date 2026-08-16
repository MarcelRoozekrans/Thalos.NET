using System.Text.Json;
using ZeroAlloc.Authorization;

namespace Thalos;

public readonly record struct ToolAuthorizationDecision(bool Allowed, string? Reason)
{
    public static ToolAuthorizationDecision Allow() => new(true, null);
    public static ToolAuthorizationDecision Deny(string reason) => new(false, reason);
}

/// <summary>Decides whether a caller may run a tool with the given arguments.</summary>
public interface IToolAuthorizer
{
    /// <summary>Decides whether <paramref name="caller"/> may run <paramref name="qualifiedToolName"/> with <paramref name="arguments"/>.</summary>
    ValueTask<ToolAuthorizationDecision> AuthorizeAsync(ISecurityContext caller, string qualifiedToolName, JsonElement arguments, CancellationToken ct);
}
