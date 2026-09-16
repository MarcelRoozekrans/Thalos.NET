using AwesomeAssertions;
using ZeroAlloc.Results;

namespace Thalos.Testing;

/// <summary>
/// Assertion helpers for <see cref="Result{TValue, TError}"/> of <see cref="AgentError"/> that close a specific trap:
/// <see cref="AgentErrorCode.Validation"/> is enum member 0, so <c>default(AgentError).Code</c> is already
/// <see cref="AgentErrorCode.Validation"/>. An unconfigured NSubstitute mock that falls through to
/// <c>default(Result&lt;TValue, AgentError&gt;)</c> is therefore indistinguishable from a genuine validation failure
/// to a bare <c>IsFailure</c> + <c>Code</c> assertion — this produced false-passing tests three separate times on
/// this branch, in three files, from two different implementers. <c>default(AgentError)</c> has a
/// <see langword="null"/> <see cref="AgentError.Message"/>, so asserting the message is non-null is what actually
/// closes the trap: a guard that never ran cannot produce a message, no matter what code the struct default happens
/// to carry.
/// </summary>
public static class ResultAssertions
{
    /// <summary>
    /// Asserts <paramref name="result"/> is a failure with the given <paramref name="code"/> and a non-null
    /// <see cref="AgentError.Message"/> — the third assertion is not decorative, see the type-level remarks.
    /// </summary>
    public static void ShouldBeFailureWith<TValue>(this Result<TValue, AgentError> result, AgentErrorCode code)
    {
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(code);
        result.Error.Message.Should().NotBeNull();
    }
}
