namespace Thalos.Workspaces;

/// <summary>Notified when files inside a run's workspace change, for a host that wants to react without watching the filesystem itself.</summary>
public interface IRunWorkspaceChangeListener
{
    /// <summary>Called with the paths, relative to the workspace root, that changed for <paramref name="runId"/>.</summary>
    /// <param name="runId">The run whose workspace changed.</param>
    /// <param name="relativePaths">The changed paths, relative to the workspace root.</param>
    void OnFilesChanged(Guid runId, IReadOnlyList<string> relativePaths);
}
