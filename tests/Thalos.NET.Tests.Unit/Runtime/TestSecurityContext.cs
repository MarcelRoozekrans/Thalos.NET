using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Runtime;

internal sealed class TestSecurityContext(string id, params string[] roles) : ISecurityContext
{
    public string Id { get; } = id;
    public IReadOnlySet<string> Roles { get; } = roles.ToHashSet(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
