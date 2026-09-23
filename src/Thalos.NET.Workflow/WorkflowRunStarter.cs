using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// Resolves a process's active version, pins every task node through an <see cref="IRunManifestResolver"/>, and
/// starts the run — in one call, so a caller never has to remember the three-step sequence itself or risk
/// starting a run whose manifest resolution it forgot to check first.
/// </summary>
/// <remarks>
/// No run row exists unless every node pinned: <see cref="StartAsync"/> resolves the whole manifest before it
/// ever calls <see cref="IWorkflowStore.StartAsync(WorkflowStartRequest,CancellationToken)"/>, so a process whose
/// agent or skill does not resolve fails without writing a partially pinned run.
/// </remarks>
public sealed class WorkflowRunStarter(IProcessDefinitionStore definitions, IRunManifestResolver resolver, IWorkflowStore store)
{
    private readonly IProcessDefinitionStore _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
    private readonly IRunManifestResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Starts a new run of <paramref name="process"/>'s active version at its declared start node, pinning every
    /// task node's agent revision and skill hash into the run's <see cref="RunManifest"/> along with
    /// <paramref name="documents"/>. Fails without starting anything when <paramref name="process"/> has no active
    /// version, when its definition does not resolve, or when any of its task nodes does not pin — see
    /// <see cref="IRunManifestResolver.ResolveAsync"/>.
    /// </summary>
    public async ValueTask<Result<Guid>> StartAsync(
        string process,
        string correlationKey,
        IReadOnlyDictionary<string, object?>? variables,
        IReadOnlyDictionary<string, string>? documents,
        CancellationToken ct)
    {
        var version = await _definitions.GetActiveVersionAsync(process, ct).ConfigureAwait(false);
        if (version is null)
        {
            return Result<Guid>.Failure($"process '{process}' has no active version");
        }

        var definition = await _definitions.GetAsync(process, version.Value, ct).ConfigureAwait(false);
        if (definition.IsFailure)
        {
            return Result<Guid>.Failure(definition.Error);
        }

        var manifest = await _resolver.ResolveAsync(definition.Value, documents, ct).ConfigureAwait(false);
        if (manifest.IsFailure)
        {
            return Result<Guid>.Failure(manifest.Error);
        }

        try
        {
            var runId = await _store.StartAsync(
                new WorkflowStartRequest
                {
                    Process = process,
                    Version = version.Value,
                    CorrelationKey = correlationKey,
                    StartNode = definition.Value.StartNode,
                    InitialVariables = variables,
                    Manifest = manifest.Value,
                },
                ct).ConfigureAwait(false);

            return Result<Guid>.Success(runId);
        }
        catch (ArgumentException ex)
        {
            // An over-cap InitialVariables bag (or a blank process/correlationKey/startNode) is a caller input
            // problem, not an infrastructure fault — the store's own guards throw for it, and this is the one
            // place that turns that throw back into the Result channel every other failure here already uses.
            return Result<Guid>.Failure(ex.Message);
        }
    }
}
