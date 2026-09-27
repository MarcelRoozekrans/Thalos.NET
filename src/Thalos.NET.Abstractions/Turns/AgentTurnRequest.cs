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

    /// <summary>
    /// Pins this turn to a specific <see cref="AgentDefinition.Revision"/> of the session's agent; <see langword="null"/> (the
    /// default) resolves the agent's current definition, exactly as before this existed. Resolved through
    /// <c>IAgentCatalog.TryGet(AgentId, string?, out AgentDefinition)</c> — a catalog that cannot serve the pinned revision
    /// fails the turn rather than silently falling back to current.
    /// </summary>
    public string? AgentRevision { get; init; }

    /// <summary>
    /// The turn's token ceiling: input plus output tokens of every model round trip, as the provider reports them. The
    /// runtime checks it before each round trip and fails the turn with
    /// <see cref="AgentErrorCode.SubagentBudgetExceeded"/> instead of starting a round trip once the turn has already
    /// used this many; a round trip already started is never cancelled, so the turn's total can end above it.
    /// <see langword="null"/> (the default) means no ceiling, for a turn with no subagent budget, such as chat or a
    /// scheduled turn (ruling R27). A value that is not positive fails the turn's validation before the session is claimed.
    /// </summary>
    public int? MaxTotalTokens { get; init; }
}
