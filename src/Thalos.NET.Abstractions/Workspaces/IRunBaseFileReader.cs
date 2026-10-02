using ZeroAlloc.Results;

namespace Thalos.Workspaces;

/// <summary>Reads files as they were at a run workspace's base commit.</summary>
public interface IRunBaseFileReader
{
    /// <summary>The text of <paramref name="relativePath"/> at the run's base commit, or null when the file does not exist there.</summary>
    ValueTask<Result<string?, AgentError>> ReadBaseFileAsync(Guid runId, string relativePath, CancellationToken ct);
}
