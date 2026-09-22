using Thalos;
using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Resolves agent names to <see cref="AgentId"/>s from a fixed map the test supplies — stands in for a real
/// resolver backed by <c>IAgentCatalog</c>, which does not exist in this unit project. <see cref="SkillExistsAsync"/>
/// is not exercised by <see cref="WorkflowNodeDispatcher"/> (only <see cref="ProcessValidator"/> uses it) but is
/// implemented anyway so this type is a complete, honest <see cref="IWorkflowReferenceResolver"/>.
/// </summary>
internal sealed class FakeWorkflowReferenceResolver(IReadOnlyDictionary<string, AgentId> agentsByName) : IWorkflowReferenceResolver
{
    public ValueTask<bool> SkillExistsAsync(string name, CancellationToken ct) =>
        ValueTask.FromResult(true);

    public ValueTask<AgentId?> ResolveAgentIdAsync(string name, CancellationToken ct) =>
        ValueTask.FromResult(agentsByName.TryGetValue(name, out var id) ? (AgentId?)id : null);
}
