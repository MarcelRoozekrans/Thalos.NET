using Microsoft.Extensions.DependencyInjection;
using Thalos.Workspaces;

namespace Thalos.Mcp;

/// <summary>
/// Finds the registered <see cref="IRunToolEndpointResolver"/> on its first use rather than when a
/// <see cref="RemoteRunToolSource"/> is built. The workspace provider that resolves endpoints also observes the source,
/// so resolving it while the source is built would be a dependency cycle. With none registered, every run has no
/// endpoint, and a run's calls are refused.
/// </summary>
/// <param name="services">The container.</param>
internal sealed class DeferredRunToolEndpointResolver(IServiceProvider services) : IRunToolEndpointResolver
{
    private IRunToolEndpointResolver? _resolver;

    public ValueTask<RunToolEndpoint?> ResolveAsync(Guid runId, string source, CancellationToken ct)
    {
        var resolver = _resolver ??= services.GetService<IRunToolEndpointResolver>();
        return resolver is null ? ValueTask.FromResult<RunToolEndpoint?>(null) : resolver.ResolveAsync(runId, source, ct);
    }
}
