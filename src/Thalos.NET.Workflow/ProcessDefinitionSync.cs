using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// Loads every document <see cref="IProcessDefinitionSource"/> currently has, validates each one against
/// <see cref="IWorkflowReferenceResolver"/>, and only then activates it through <see cref="IProcessDefinitionStore"/>.
/// This is what makes hot-reload safe: a document that fails to load or validate is reported and left alone — the
/// version already active for that process, if any, keeps running unchanged. A broken edit to a process file is
/// rejected, never made runnable.
/// </summary>
public sealed class ProcessDefinitionSync(
    IProcessDefinitionSource source,
    IProcessDefinitionStore store,
    IWorkflowReferenceResolver resolver)
{
    private readonly IProcessDefinitionSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly IProcessDefinitionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IWorkflowReferenceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <summary>
    /// Loads, validates and activates every document <see cref="IProcessDefinitionSource.ReadAllAsync"/> returns.
    /// Returns the count of definitions activated. A document that fails to load
    /// (<see cref="ProcessLoader.Load"/>) or validate (<see cref="ProcessValidator.ValidateAsync"/>) contributes
    /// its error to the returned <see cref="Result{T}.Error"/> instead and is never activated — but every other
    /// document in the same batch is still processed: one malformed process file does not stop the rest of the
    /// repository from syncing, and the batch result is a failure overall only because that one document's error
    /// is in it, not because syncing stopped early.
    /// </summary>
    public async ValueTask<Result<int>> SyncAsync(CancellationToken ct)
    {
        var documents = await _source.ReadAllAsync(ct).ConfigureAwait(false);
        var activated = 0;
        var errors = new List<string>();

        foreach (var document in documents)
        {
            var loaded = ProcessLoader.Load(document.Yaml);
            if (loaded.IsFailure)
            {
                errors.Add($"{document.SourcePath}: {loaded.Error}");
                continue;
            }

            var validated = await ProcessValidator.ValidateAsync(loaded.Value, _resolver, ct).ConfigureAwait(false);
            if (validated.IsFailure)
            {
                errors.Add($"{document.SourcePath}: {validated.Error}");
                continue;
            }

            await _store.UpsertAndActivateAsync(validated.Value, document.Yaml, ct).ConfigureAwait(false);
            activated++;
        }

        return errors.Count == 0
            ? Result<int>.Success(activated)
            : Result<int>.Failure(string.Join("; ", errors));
    }
}
