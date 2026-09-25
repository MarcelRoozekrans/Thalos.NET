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
    /// Starts a new run of <see cref="WorkflowRunStartOptions.Process"/>'s active version at its declared start
    /// node, pinning every task node's agent revision and skill hash into the run's <see cref="RunManifest"/>
    /// along with <see cref="WorkflowRunStartOptions.Documents"/>. Fails without starting anything when the
    /// process has no active version, when its definition does not resolve, or when any of its task nodes does
    /// not pin — see <see cref="IRunManifestResolver.ResolveAsync"/>.
    /// </summary>
    public async ValueTask<Result<Guid>> StartAsync(WorkflowRunStartOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Every start names its starter (ruling R26): refused before the store is ever called, so a caller sees
        // this fail loudly rather than the store's own guard throwing with a different parameter name. No
        // explicit paramName: CallerArgumentExpression supplies "options.StartedBy" itself, which is exactly
        // what WorkflowRunStarterTests.A_start_with_no_starter_is_refused_before_the_store_is_called asserts on.
        ArgumentNullException.ThrowIfNull(options.StartedBy);

        var process = options.Process;
        var correlationKey = options.CorrelationKey;

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

        var manifest = await _resolver.ResolveAsync(definition.Value, options.Documents, ct).ConfigureAwait(false);
        if (manifest.IsFailure)
        {
            return Result<Guid>.Failure(manifest.Error);
        }

        // The store itself now returns a Result: an over-cap InitialVariables bag or a colliding caller-supplied
        // RunId are the caller-input problems the store's own Result.Failure already names, on the same channel
        // every other failure here uses — nothing here needs to catch anything to translate one. A structurally
        // broken request (a blank process/correlationKey/startNode, a missing StartedBy) is a programming error,
        // not a condition this method exists to recover from, and is left to propagate as a thrown exception.
        return await _store.StartAsync(
            new WorkflowStartRequest
            {
                Process = process,
                Version = version.Value,
                CorrelationKey = correlationKey,
                StartNode = definition.Value.StartNode,
                InitialVariables = options.Variables,
                Manifest = manifest.Value,
                StartedBy = options.StartedBy,
                RunId = options.RunId,
            },
            ct).ConfigureAwait(false);
    }
}
