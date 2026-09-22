using ZeroAlloc.Authorization;

namespace Thalos.Tests.Workflow;

/// <summary>Minimal <see cref="ISecurityContext"/> for tests that need to hand one to <see cref="SubagentRunRequest.Caller"/>.</summary>
internal sealed class FakeSecurityContext(string id) : ISecurityContext
{
    public string Id { get; } = id;
    public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
