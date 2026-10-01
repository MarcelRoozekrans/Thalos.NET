using ZeroAlloc.Results;

namespace Thalos.Workspaces;

/// <summary>Where a run's tools are served, and the token that authorises calls to them.</summary>
/// <param name="Endpoint">The MCP endpoint.</param>
/// <param name="BearerToken">The bearer token for the endpoint.</param>
public sealed record RunToolEndpoint(Uri Endpoint, string BearerToken);

/// <summary>Resolves the MCP endpoint serving a run's tools.</summary>
public interface IRunToolEndpointResolver
{
    /// <summary>The MCP endpoint serving <paramref name="source"/>'s tools for the run, or null when it has none (not ready, parked, removed).</summary>
    ValueTask<RunToolEndpoint?> ResolveAsync(Guid runId, string source, CancellationToken ct);
}
