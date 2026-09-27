using Thalos;
using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Resolves agent names to <see cref="AgentId"/>s from a fixed map the test supplies — stands in for a real
/// resolver backed by <c>IAgentCatalog</c>, which does not exist in this unit project. <see cref="SkillExistsAsync"/>
/// is not exercised by <see cref="WorkflowNodeDispatcher"/> (only <see cref="ProcessValidator"/> uses it) but is
/// implemented anyway so this type is a complete, honest <see cref="IWorkflowReferenceResolver"/>.
/// <see cref="HostActionExistsAsync"/> answers from <paramref name="hostActions"/>, the action names the test says
/// are registered — none when it passes none — and, like <see cref="WorkflowReferenceResolver"/>, refuses a blank
/// name with <see cref="ArgumentException"/>, so a validator that asked about one would be caught here too.
/// </summary>
internal sealed class FakeWorkflowReferenceResolver(
    IReadOnlyDictionary<string, AgentId> agentsByName,
    IReadOnlySet<string>? hostActions = null) : IWorkflowReferenceResolver
{
    public ValueTask<bool> SkillExistsAsync(string name, CancellationToken ct) =>
        ValueTask.FromResult(true);

    public ValueTask<AgentId?> ResolveAgentIdAsync(string name, CancellationToken ct) =>
        ValueTask.FromResult(agentsByName.TryGetValue(name, out var id) ? (AgentId?)id : null);

    public ValueTask<bool> HostActionExistsAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(hostActions?.Contains(name) ?? false);
    }
}
