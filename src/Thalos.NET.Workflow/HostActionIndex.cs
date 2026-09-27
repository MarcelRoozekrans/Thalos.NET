using System.Collections.Frozen;

namespace Thalos.Workflow;

/// <summary>
/// Indexes a host's <see cref="IWorkflowHostAction"/> registrations by <see cref="IWorkflowHostAction.Name"/>,
/// compared ordinally. <see cref="WorkflowReferenceResolver"/> and <see cref="WorkflowNodeDispatcher"/> both build
/// their lookup here, so the question "is this action registered" has one answer at load time and at dispatch time.
/// </summary>
internal static class HostActionIndex
{
    /// <summary>
    /// Builds the index, throwing <see cref="ArgumentException"/> for a registration that could never be dispatched
    /// unambiguously: a <see langword="null"/> entry, a blank <see cref="IWorkflowHostAction.Name"/>, or two
    /// actions under the same name. A duplicate is refused rather than resolved first-wins or last-wins, because
    /// which of the two a node ran would then depend on registration order, which nothing in a process file shows.
    /// </summary>
    public static FrozenDictionary<string, IWorkflowHostAction> Build(IEnumerable<IWorkflowHostAction> hostActions, string paramName)
    {
        ArgumentNullException.ThrowIfNull(hostActions, paramName);

        var index = new Dictionary<string, IWorkflowHostAction>(StringComparer.Ordinal);
        foreach (var action in hostActions)
        {
            if (action is null)
            {
                throw new ArgumentException("A host action registration is null.", paramName);
            }

            if (string.IsNullOrWhiteSpace(action.Name))
            {
                throw new ArgumentException($"Host action {action.GetType().FullName} has a blank Name.", paramName);
            }

            if (!index.TryAdd(action.Name, action))
            {
                throw new ArgumentException(
                    $"Two host actions are registered under the name '{action.Name}': {index[action.Name].GetType().FullName} and {action.GetType().FullName}.",
                    paramName);
            }
        }

        return index.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
