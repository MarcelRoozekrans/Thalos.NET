namespace Thalos.Workflow;

/// <summary>
/// What a successful <see cref="IWorkflowHostAction.RunAsync"/> reports for an action node — the host-code
/// counterpart of the outcome and variables a task node's agent reports through its outcome tool.
/// </summary>
/// <param name="Outcome">
/// The outcome the action finished with. It must be one of the action node's declared
/// <see cref="ProcessNode.Outcomes"/>: an action node is required to declare them, and
/// <see cref="WorkflowInterpreter.Advance"/> refuses an outcome outside that set rather than guess a branch.
/// </param>
/// <param name="Variables">
/// Variables to merge into the run's <see cref="WorkflowRun.Variables"/>, under the same rules as a task node's
/// reported variables. An empty dictionary merges nothing and clears nothing.
/// </param>
/// <exception cref="ArgumentException">
/// A value in <paramref name="Variables"/> is not JSON-shaped; see <see cref="WorkflowVariableJson.Validate"/>.
/// </exception>
public sealed record HostActionResult(string Outcome, IReadOnlyDictionary<string, object?> Variables)
{
    private readonly IReadOnlyDictionary<string, object?> _variables = Checked(Variables);

    /// <summary>
    /// Variables to merge into the run's <see cref="WorkflowRun.Variables"/>; every value JSON-shaped, as
    /// <see cref="WorkflowVariableJson.Validate"/> checks.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Variables
    {
        get => _variables;
        init => _variables = Checked(value);
    }

    private static IReadOnlyDictionary<string, object?> Checked(IReadOnlyDictionary<string, object?> variables)
    {
        WorkflowVariableJson.Validate(variables, nameof(Variables));
        return variables;
    }
}
