using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Runtime;

namespace Thalos.Skills;

/// <summary>
/// Appends the agent's skill catalogue — the names and descriptions of the procedures it may load — to its instructions on
/// every run, via <see cref="AIContext.Instructions"/>. Building the block is a dictionary lookup (<see cref="SkillCatalogue"/>
/// renders once per sync per glob set), and it never fails a turn: any error is logged, a
/// <see cref="SkillCatalogueFailedEvent"/> is published and the turn proceeds without a catalogue.
/// </summary>
/// <remarks>
/// Unlike memory recall there is no caller requirement: skills are not per-user data, so an anonymous caller sees the same
/// catalogue. Skill bodies come from git rather than model output, so they are deliberately not passed through
/// <see cref="IUntrustedContentScanner"/> — the only defence is the tag neutralisation in <see cref="SkillCatalogue"/>.
/// Whoever can merge a SKILL.md can steer the agent, which is the trust boundary of merging code.
/// </remarks>
public sealed partial class SkillContextProvider(
    SkillCatalogue catalogue,
    IReadOnlyList<string> globs,
    AgentEventHub hub,
    ILogger<SkillContextProvider>? logger = null) : AIContextProvider
{
    private readonly ILogger _logger = logger ?? NullLogger<SkillContextProvider>.Instance;

    /// <summary>The globs this provider renders for (tests: verifies the per-agent copy).</summary>
    internal IReadOnlyList<string> Globs => globs;

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return catalogue.Render(globs) is { } block ? new AIContext { Instructions = block } : new AIContext();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCatalogueFailed(_logger, ex.Message, ex);
            await SkillEvents.PublishAsync(hub, static (s, t) => new SkillCatalogueFailedEvent(s, t, AgentErrorCode.SkillStoreFailed), CancellationToken.None).ConfigureAwait(false);
            return new AIContext();
        }
    }

    [LoggerMessage(EventId = 568, Level = LogLevel.Warning, Message = "The skill catalogue could not be rendered; the turn continues without one: {Error}")]
    private static partial void LogCatalogueFailed(ILogger logger, string error, Exception exception);
}
