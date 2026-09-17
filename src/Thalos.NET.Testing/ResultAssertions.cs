using AwesomeAssertions;
using ZeroAlloc.Results;

namespace Thalos.Testing;

// Development note (not published): this exact gap — a bare IsFailure + Code assertion passing against an
// unconfigured mock's default value — produced false-passing tests three separate times on this branch, in three
// different files, written by two different implementers. That is the motivation for making the non-null Message
// check part of the helper instead of leaving it to each call site to remember.

/// <summary>
/// Assertion helpers for <see cref="Result{TValue, TError}"/> of <see cref="AgentError"/>.
/// </summary>
/// <remarks>
/// <see cref="AgentErrorCode.Validation"/> is enum member 0, which makes it the CLR default value for
/// <see cref="AgentErrorCode"/>. As a result, <c>default(AgentError).Code</c> already equals
/// <see cref="AgentErrorCode.Validation"/>, and <c>default(Result{TValue, AgentError})</c> — the value an
/// unconfigured mock or an uninitialized field falls through to — is indistinguishable from a genuine validation
/// failure under an assertion that checks only <c>IsFailure</c> and <c>Code</c>. <c>default(AgentError)</c> does,
/// however, have a <see langword="null"/> <see cref="AgentError.Message"/>, since no code path has actually
/// constructed the error. Asserting that the message is non-null therefore distinguishes a real failure from an
/// uninitialized default: a guard that never ran cannot have produced a message.
/// </remarks>
public static class ResultAssertions
{
    /// <summary>
    /// Asserts that <paramref name="result"/> is a failure with the given <paramref name="code"/> and a non-null
    /// <see cref="AgentError.Message"/>.
    /// </summary>
    /// <remarks>
    /// The message check is not decorative: it is what rules out <c>default(AgentError)</c> masquerading as a
    /// genuine <see cref="AgentErrorCode.Validation"/> failure. See the type-level remarks for why that matters.
    /// </remarks>
    public static void ShouldBeFailureWith<TValue>(this Result<TValue, AgentError> result, AgentErrorCode code)
    {
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(code);
        result.Error.Message.Should().NotBeNull();
    }
}
