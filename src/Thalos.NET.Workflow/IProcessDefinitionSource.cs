namespace Thalos.Workflow;

/// <summary>
/// Where process definitions come from, as raw YAML text plus an identifying source path for error messages.
/// Activation, pinning and retention are engine rules — <see cref="ProcessDefinitionSync"/> enforces them the
/// same way regardless of backend — but where the YAML itself originates is host policy: Thalos declares this
/// contract and a host supplies the implementation. The real implementation is git-backed
/// (Daedalus's workflow package, a later task); Thalos.NET must never reach for a filesystem or git itself.
/// </summary>
public interface IProcessDefinitionSource
{
    /// <summary>
    /// Reads every process definition document currently available from this source. Called once per
    /// <see cref="ProcessDefinitionSync.SyncAsync"/> pass — a source that lists incompletely (a partial git
    /// checkout, an unreadable directory) is a host-side concern; this contract makes no promise about it.
    /// </summary>
    ValueTask<IReadOnlyList<ProcessDocument>> ReadAllAsync(CancellationToken ct);
}

/// <summary>One process definition document as read from its source, before <see cref="ProcessLoader.Load"/> parses it.</summary>
/// <param name="SourcePath">
/// Where this document came from — a file path, a blob path, whatever names it well enough for a load or
/// validation error to point at. Not parsed or otherwise trusted; carried through only for error messages.
/// </param>
/// <param name="Yaml">The raw YAML text, verbatim — exactly what <see cref="ProcessLoader.Load"/> consumes.</param>
public sealed record ProcessDocument(string SourcePath, string Yaml);
