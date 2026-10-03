using ZeroAlloc.Results;

namespace Thalos.Tests.Mcp;

/// <summary>Assertions on a readiness wait that name why a server did not become ready, rather than a bare "Expected True".</summary>
internal static class ReadinessAssertions
{
    /// <summary>Asserts <paramref name="waited"/> succeeded; a failure reports the wait's own error message after <paramref name="because"/>.</summary>
    public static void ShouldBeReady(this UnitResult<AgentError> waited, string because = "") =>
        waited.IsSuccess.Should().BeTrue(waited.IsFailure ? $"{because} {waited.Error.Message}".Trim() : because);
}
