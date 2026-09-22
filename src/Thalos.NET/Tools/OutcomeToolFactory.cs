using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;

namespace Thalos.Tools;

/// <summary>
/// Builds the one synthetic tool a turn carrying <see cref="AgentTurnRequest.RequiredOutcome"/> is offered, already
/// wrapped for authorization and audit.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="IToolCatalog"/>: the catalogue answers "which of the host's configured
/// tools may <em>this agent</em> see", a question asked once per agent build and cached with the agent. An outcome
/// tool is the opposite — per <em>turn</em>, never cached, and different for two turns of the same agent. Folding it
/// into the catalogue would either leak one node's outcome set into another node's agent or force the agent cache to
/// key on it.
/// </remarks>
public interface IOutcomeToolFactory
{
    /// <summary>
    /// Returns the tool for <paramref name="schema"/>, or <see cref="AgentErrorCode.Validation"/> when the schema
    /// cannot be expressed as one (a name no provider would accept, or a value set that cannot form an
    /// <c>enum</c>). The returned tool is already wrapped so that invoking it is authorized and recorded, exactly
    /// like a tool that came from an <see cref="IToolSource"/>.
    /// </summary>
    Result<AITool, AgentError> Create(OutcomeToolSchema schema);
}

/// <summary>
/// Default <see cref="IOutcomeToolFactory"/>: an <see cref="OutcomeTool"/> inside an
/// <see cref="AuthorizingAIFunction"/>, built from the same collaborators <see cref="ToolCatalog"/> wraps every
/// other tool with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the wrapper is not optional.</b> Two independent reasons, either of which alone would settle it.
/// </para>
/// <para>
/// Authorization: <see cref="AuthorizingAIFunction"/> is the enforcement point, and a host's
/// <see cref="IToolAuthorizer"/> binds policies by tool-name pattern. A synthetic tool that skipped it would be the
/// one tool in the process no policy can reach — and it is not an inert one: calling it is how a workflow decides
/// which branch runs, which gate opens, whether a loop goes round again. "Thalos generated it, so it is safe" is
/// exactly the reasoning that puts an unauditable capability in front of a model. Nothing about being synthetic
/// makes it exempt.
/// </para>
/// <para>
/// Audit and mechanism: the wrapper is also what records the call into <c>TurnScope</c>, which is what populates
/// <see cref="AgentTurnResult.ToolCalls"/> — the only place the outcome is read from. Bypassing it would not merely
/// lose the audit trail; the reported outcome would never reach the caller at all, and every constrained node would
/// fail as "completed without reporting an outcome". The audited path and the working path are the same path.
/// </para>
/// <para>
/// The consequence is accepted rather than worked around: a host whose policy denies this tool gets a turn in which
/// the model is told "Tool call denied", and the run fails for want of a usable outcome. That is the correct
/// reading of a host that configured the denial.
/// </para>
/// </remarks>
[Singleton(As = typeof(IOutcomeToolFactory))] // interface only (the default would also register the concrete type as a second instance)
public sealed class OutcomeToolFactory : IOutcomeToolFactory
{
    private readonly IToolAuthorizer _authorizer;
    private readonly IAgentNotificationPublisher _publisher;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuthorizingAIFunction> _functionLogger;

    /// <summary>Creates a factory over the collaborators every authorized tool invocation needs.</summary>
    public OutcomeToolFactory(
        IToolAuthorizer authorizer,
        IAgentNotificationPublisher publisher,
        TimeProvider clock,
        ILoggerFactory? loggerFactory = null)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _functionLogger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AuthorizingAIFunction>();
    }

    /// <inheritdoc />
    public Result<AITool, AgentError> Create(OutcomeToolSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (OutcomeTool.Validate(schema) is { } error)
        {
            return Result<AITool, AgentError>.Failure(error);
        }

        // The schema's name is already the qualified name: an outcome tool belongs to no IToolSource, so there is no
        // "{source}__" prefix for the catalogue to add and the caller names it in full ("workflow__report_outcome").
        var tool = new OutcomeTool(schema);
        return Result<AITool, AgentError>.Success(
            new AuthorizingAIFunction(tool, tool.Name, _authorizer, _publisher, _clock, _functionLogger));
    }
}
