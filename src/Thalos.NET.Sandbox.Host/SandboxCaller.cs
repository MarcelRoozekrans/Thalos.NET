using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Thalos.Runtime;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;

namespace Thalos.Sandbox.Host;

/// <summary>
/// The caller every MCP call in a sandbox runs as. Its only claim is <see cref="RunWorkspaceClaims.RunId"/>, the
/// sandbox's own run, and it has no roles. Authorization happened on the API before the call was forwarded; the
/// sandbox only confines the call to its run's workspace.
/// </summary>
/// <param name="runId">The sandbox's run.</param>
internal sealed class SandboxCaller(Guid runId) : ISecurityContext
{
    /// <inheritdoc />
    public string Id { get; } = $"sandbox:{runId:D}";

    /// <inheritdoc />
    public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Claims { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = runId.ToString("D") };
}

/// <summary>
/// Runs a route's tool inside a <see cref="TurnScope"/> whose caller is the <see cref="SandboxCaller"/>, begun with the
/// same <see cref="TurnScope.Begin"/> the turn runner uses, so a tool reading <see cref="TurnScope.Current"/> sees it.
/// An optional step, the restore check, runs inside the scope before the tool.
/// </summary>
/// <param name="inner">The tool.</param>
/// <param name="caller">The sandbox's caller.</param>
/// <param name="before">Awaited before every call, or null.</param>
internal sealed class SandboxScopedTool(McpServerTool inner, SandboxCaller caller, Func<CancellationToken, ValueTask>? before) : DelegatingMcpServerTool(inner)
{
    /// <inheritdoc />
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), caller);
        if (before is not null)
        {
            await before(cancellationToken).ConfigureAwait(false);
        }

        return await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
