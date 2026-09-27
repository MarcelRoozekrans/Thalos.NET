using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Thalos.Runtime;

/// <summary>
/// Enforces <see cref="AgentTurnRequest.MaxTotalTokens"/>. Before each model round trip it throws
/// <see cref="AgentTurnException"/> (<see cref="AgentErrorCode.SubagentBudgetExceeded"/>) when the ambient
/// <see cref="TurnScope"/> has already used at least its ceiling; after the round trip it adds the input plus output
/// tokens the provider reported to the scope. The runtime's exception mapping turns the throw into the turn's error.
/// A round trip already started is never cancelled, so a turn can end above its ceiling by at most what its last
/// round trips spent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placement.</b> <see cref="AgentFactory"/> wraps the provider's client in this directly, below every
/// <see cref="IChatClientDecorator"/>, so MAF's function-invocation loop passes through it on every round trip and no
/// decorator that answers or short-circuits a call can skip the check. Provider-side wrappers that normalise usage,
/// such as a prompt-cache translator inside the provider's own client, sit below it, so what is counted is the
/// provider's normalised report.
/// </para>
/// <para>
/// <b>What counts.</b> Every input token, cached or not, as the provider reports it in
/// <see cref="UsageDetails.InputTokenCount"/>, plus <see cref="UsageDetails.OutputTokenCount"/>. A count the provider
/// does not report is counted as zero: no estimate is made. On the streaming path usage arrives as
/// <see cref="UsageContent"/> updates, each counted as it passes through. A stream that ends early, because the
/// consumer abandoned it or it threw, counts only the usage already seen, and the turn ends either way.
/// </para>
/// <para>
/// <b>Concurrency.</b> Round trips of one turn may run concurrently. The count and the round-trip number are updated
/// atomically, so none is lost; concurrent round trips that each pass the check before any of them reports usage all
/// start, which is the same "a call already made is never cancelled" overshoot.
/// </para>
/// </remarks>
internal sealed class TurnBudgetChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var scope = Check();
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        if (response.Usage is { } usage)
        {
            scope?.AddTokens(Tokens(usage));
        }

        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scope = Check();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usage)
                {
                    scope?.AddTokens(Tokens(usage.Details));
                }
            }

            yield return update;
        }
    }

    /// <summary>Input plus output tokens; a count the provider left out is zero.</summary>
    private static long Tokens(UsageDetails usage) => (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0);

    /// <summary>
    /// Numbers the round trip about to start and throws when the ambient turn has already reached its ceiling. Returns
    /// the scope to count into, or null when no turn is in progress.
    /// </summary>
    private static TurnScope? Check()
    {
        var scope = TurnScope.Current;
        if (scope?.MaxTotalTokens is not { } max)
        {
            return scope;
        }

        var trip = scope.NextRoundTrip();
        var used = scope.TokensSoFar;
        return used >= max
            ? throw new AgentTurnException(AgentError.SubagentBudgetExceeded(max, trip, used))
            : scope;
    }
}
