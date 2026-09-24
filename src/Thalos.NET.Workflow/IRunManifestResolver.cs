using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// Builds the <see cref="RunManifest"/> a run is started with: resolves every task node's <c>agent:</c> name to
/// the agent definition and revision it is running, and its <c>skill:</c> name to the exact skill body's content
/// hash, so the run stays pinned to that build of each even if the agent or skill is edited or republished later.
/// </summary>
public interface IRunManifestResolver
{
    /// <summary>
    /// Resolves every task node in <paramref name="process"/> to a <see cref="NodePin"/>, carrying
    /// <paramref name="documents"/> into the returned <see cref="RunManifest"/> unchanged. Gate and terminal
    /// nodes are not pinned — they have no agent or skill. Fails without pinning anything if any task node's
    /// agent or skill does not resolve, naming the node and what was missing.
    /// </summary>
    ValueTask<Result<RunManifest>> ResolveAsync(ProcessDefinition process, IReadOnlyDictionary<string, string>? documents, CancellationToken ct);
}
