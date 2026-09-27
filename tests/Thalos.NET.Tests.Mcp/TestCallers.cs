using Thalos.Runtime;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Mcp;

/// <summary>Callers, and a turn scope that makes one the ambient caller, for routing tests.</summary>
internal static class TestCallers
{
    /// <summary>Makes <paramref name="caller"/> the turn's caller on this async flow until disposed, as the runtime does for a turn.</summary>
    public static IDisposable BeginTurn(ISecurityContext caller) => TurnScope.Begin(SessionId.New(), TurnId.New(), caller);

    /// <summary>A caller carrying <paramref name="runId"/> as its run claim, as a host builds one for a workflow run's node.</summary>
    public static TestCaller RunCaller(Guid runId) => new($"run:{runId:D}", new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = runId.ToString("D") });
}

internal sealed class TestCaller(string id, IReadOnlyDictionary<string, string>? claims = null) : ISecurityContext
{
    public string Id { get; } = id;

    public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Claims { get; } = claims ?? new Dictionary<string, string>(StringComparer.Ordinal);
}
