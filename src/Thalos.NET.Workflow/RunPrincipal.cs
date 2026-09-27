namespace Thalos.Workflow;

/// <summary>
/// Who a run was started or resumed by: a principal id and the roles it held at that moment. Written by host code
/// only — never a run variable, so no node's outcome report can reach it.
/// </summary>
/// <param name="Id">The principal's stable identity, for example a Keycloak subject or a host's own system id.</param>
/// <param name="Roles">The roles the principal held at the moment it started or resumed the run.</param>
public sealed record RunPrincipal(string Id, IReadOnlyList<string> Roles)
{
    /// <summary>
    /// Human-readable name for audit text. <see langword="null"/> = the host knows none, and audit text falls
    /// back to <see cref="Id"/>. Never used for authorization.
    /// </summary>
    public string? DisplayName { get; init; }
}
