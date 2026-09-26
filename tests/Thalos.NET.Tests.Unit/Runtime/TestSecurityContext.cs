using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Runtime;

internal sealed class TestSecurityContext(string id, params string[] roles) : ISecurityContext
{
    public string Id { get; } = id;
    public IReadOnlySet<string> Roles { get; } = roles.ToHashSet(StringComparer.Ordinal);

    /// <summary>Empty unless set via an object initializer, e.g. <c>new TestSecurityContext("id") { Claims = myClaims }</c>.</summary>
    public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
