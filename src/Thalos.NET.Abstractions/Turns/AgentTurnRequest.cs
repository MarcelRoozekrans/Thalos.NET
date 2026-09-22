using ZeroAlloc.Authorization;
using ZeroAlloc.Validation;

namespace Thalos;

/// <summary>One user message for a session. <see cref="Caller"/> is never inferred by Thalos — the channel supplies it.</summary>
[Validate]
public sealed record AgentTurnRequest(
    SessionId SessionId,
    [property: NotEmpty] string Text,
    ISecurityContext Caller)
{
    /// <summary>
    /// When set, this turn is additionally offered one synthetic tool — named
    /// <see cref="OutcomeToolSchema.ToolName"/>, taking a single <see cref="OutcomeToolSchema.ArgumentName"/>
    /// argument whose JSON schema constrains it to exactly <see cref="OutcomeToolSchema.AllowedValues"/> — so the
    /// turn can report a closed-set result as a tool call rather than as free text. The tool is offered for this
    /// turn only: it is passed as run options and never enters the agent's cached tool set, so the same agent
    /// definition can serve two callers with different outcome sets, or none, concurrently.
    /// <see langword="null"/> (the default) leaves the turn exactly as it was before this existed — the same tool
    /// list, the same request to the provider.
    /// </summary>
    public OutcomeToolSchema? RequiredOutcome { get; init; }
}
