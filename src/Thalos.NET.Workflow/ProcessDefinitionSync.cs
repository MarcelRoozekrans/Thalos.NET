using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// Loads every document <see cref="IProcessDefinitionSource"/> currently has, validates each one against
/// <see cref="IWorkflowReferenceResolver"/>, and only then activates it through <see cref="IProcessDefinitionStore"/>.
/// This is what makes hot-reload safe: a document that fails to load or validate is reported and left alone — the
/// version already active for that process, if any, keeps running unchanged. A broken edit to a process file is
/// rejected, never made runnable. An edit that is perfectly valid but reuses a version number already stored with
/// different content is rejected too, by the store rather than by this type: a version a run may be pinned to is
/// immutable, so the fix is to bump the version, not to rewrite the graph underneath a live run.
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
    /// <remarks>
    /// One exception to "every other document is still processed": two documents in the same batch that load
    /// successfully and declare the same <see cref="ProcessDefinition.Name"/> — a stale file left behind
    /// alongside its replacement, say — fail the <em>whole</em> batch before anything is activated, named in
    /// <see cref="FindDuplicateProcessName"/>. Activating either one would make which version ends up active
    /// depend on listing order rather than on anything a reviewer chose, which is exactly the kind of
    /// back-door-runnable state this type exists to prevent — the same property Step 2's rejection test protects
    /// against a single broken document.
    /// </remarks>
    public async ValueTask<Result<int>> SyncAsync(CancellationToken ct)
    {
        var documents = await _source.ReadAllAsync(ct).ConfigureAwait(false);
        var errors = new List<string>();
        var loaded = new List<(ProcessDocument Document, ProcessDefinition Definition)>();

        foreach (var document in documents)
        {
            var result = ProcessLoader.Load(document.Yaml);
            if (result.IsFailure)
            {
                errors.Add($"{document.SourcePath}: {result.Error}");
                continue;
            }

            loaded.Add((document, result.Value));
        }

        var duplicate = FindDuplicateProcessName(loaded);
        if (duplicate is not null)
        {
            errors.Add(duplicate);
            return Result<int>.Failure(string.Join("; ", errors));
        }

        var activated = await ActivateAllAsync(loaded, errors, ct).ConfigureAwait(false);

        return errors.Count == 0
            ? Result<int>.Success(activated)
            : Result<int>.Failure(string.Join("; ", errors));
    }

    private async ValueTask<int> ActivateAllAsync(
        List<(ProcessDocument Document, ProcessDefinition Definition)> loaded, List<string> errors, CancellationToken ct)
    {
        var activated = 0;
        foreach (var (document, definition) in loaded)
        {
            var validated = await ProcessValidator.ValidateAsync(definition, _resolver, ct).ConfigureAwait(false);
            if (validated.IsFailure)
            {
                errors.Add($"{document.SourcePath}: {validated.Error}");
                continue;
            }

            // The store enforces one rule of its own: a stored version is immutable. Editing a process file
            // without bumping its version is refused here rather than rewriting the definition a live run is
            // pinned to, and is reported exactly like a load or validation error — one bad document, the rest of
            // the batch still syncs.
            var activation = await _store.UpsertAndActivateAsync(validated.Value, document.Yaml, ct).ConfigureAwait(false);
            if (activation.IsFailure)
            {
                errors.Add($"{document.SourcePath}: {activation.Error}");
                continue;
            }

            activated++;
        }

        return activated;
    }

    /// <summary>
    /// The batch-level integrity check that runs before any activation: two documents that both loaded
    /// successfully and name the same <see cref="ProcessDefinition.Name"/> (regardless of version) leave whichever
    /// one <see cref="ActivateAllAsync"/> happened to process last as the active version — a listing-order
    /// accident deciding which definition wins, silently, in the exact type whose job is to stop that. Names both
    /// documents' <see cref="ProcessDocument.SourcePath"/> so the fix is obvious from the error alone.
    /// </summary>
    private static string? FindDuplicateProcessName(List<(ProcessDocument Document, ProcessDefinition Definition)> loaded)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (document, definition) in loaded)
        {
            if (seen.TryGetValue(definition.Name, out var first))
            {
                return $"process '{definition.Name}' is declared by more than one document ('{first}' and '{document.SourcePath}') — refusing to activate anything in this batch rather than let listing order decide which one wins";
            }

            seen[definition.Name] = document.SourcePath;
        }

        return null;
    }
}
