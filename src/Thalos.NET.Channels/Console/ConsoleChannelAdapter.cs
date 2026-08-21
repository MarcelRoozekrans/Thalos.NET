using System.Collections.Concurrent;

namespace Thalos.Channels.Console;

/// <summary>
/// Writes turn output to a <see cref="TextWriter"/>. The pump renders cumulatively, so this adapter prints only the
/// suffix it has not printed yet — a terminal cannot edit what it already emitted.
/// </summary>
/// <remarks>
/// <para>
/// The conversation id is not part of what gets written: a console has exactly one conversation, and there is only
/// one stream to write it to.
/// </para>
/// <para>
/// <b>The terminal event carries the answer, not a newline.</b> A <see cref="TurnCompletedEvent"/> is rendered from
/// <see cref="AgentTurnResult.Text"/> — the authoritative, complete reply — diffed against what has already been
/// printed, exactly as <c>TelegramChannelAdapter</c> does. Treating it as a bare line break instead would silently
/// truncate every answer whose last deltas were suppressed by the pump's coalescer, which under the default
/// <see cref="ChannelOptions.FlushInterval"/> of one second is the tail of essentially every turn (and, for a turn
/// shorter than a second, everything after its first delta).
/// </para>
/// <para>
/// <b>Thread safety.</b> Deliveries to one conversation are serialised by a per-conversation gate, the same shape
/// <c>TelegramChannelAdapter</c> uses and for the same reason: the pump sends operator notices (<c>/help</c>, the
/// busy notice) from its reader loop while a turn streams on a detached task, so two deliveries genuinely do race.
/// <c>System.Console.Out</c> is itself synchronised and this adapter only ever appends, so the failure it
/// prevents is garbled interleaving and a corrupted <c>_printed</c> diff base rather than a lost answer. A console
/// host has exactly one conversation, so in practice this gate serialises every delivery.
/// </para>
/// </remarks>
public sealed class ConsoleChannelAdapter(TextWriter writer) : IChannelAdapter
{
    /// <summary>
    /// One gate per conversation. Entries are never removed, for the same reason they are not in the Telegram
    /// adapter: a conversation id is stable, so this is bounded by the number of conversations the host ever serves
    /// (one, for a console) rather than by turns — and removing a gate another delivery is waiting on is the bug.
    /// </summary>
    private readonly ConcurrentDictionary<ConversationId, SemaphoreSlim> _gates = new();

    private string _printed = string.Empty;

    /// <inheritdoc />
    public string ChannelId => "console";

    /// <inheritdoc />
    public async ValueTask DeliverAsync(ConversationId conversationId, AgentEvent agentEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        var gate = _gates.GetOrAdd(conversationId, static _ => new SemaphoreSlim(1, 1));

        // Tracked separately from the acquisition: releasing a semaphore that was never taken (because the wait was
        // cancelled) would hand a second delivery a permit that does not exist.
        var held = false;

        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            held = true;
            await DeliverCoreAsync(agentEvent, ct).ConfigureAwait(false);
        }
        finally
        {
            if (held)
            {
                gate.Release();
            }
        }
    }

    private async Task DeliverCoreAsync(AgentEvent agentEvent, CancellationToken ct)
    {
        switch (agentEvent)
        {
            case TextDeltaEvent delta:
                await WriteBodyAsync(delta.Text).ConfigureAwait(false);
                break;

            // The buffered result, not the last delta the coalescer let through: the trailing renders of a turn are
            // suppressed whenever FlushInterval is positive, so this is the only event carrying the whole answer.
            case TurnCompletedEvent completed:
                await FinishAsync(completed.Result.Text).ConfigureAwait(false);
                break;

            case TurnFailedEvent failed:
                await FinishAsync(Failure(failed)).ConfigureAwait(false);
                break;

            default:
                break;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The body a failed turn ends on: the error underneath whatever the turn had already printed, never instead of
    /// it. Same shape as the Telegram adapter's — replacing a partial answer with a bare error throws away output
    /// the operator can still use, and a terminal cannot unprint it in any case.
    /// </summary>
    private string Failure(TurnFailedEvent failed)
    {
        // Plain interpolation, not string.Create(CultureInfo.InvariantCulture, …): both holes are culture-invariant
        // (an enum name and a string), which is what MA0185 asks for here.
        var notice = $"⚠ The turn failed ({failed.Error.Code}): {failed.Error.Message}";

        return _printed.Length > 0 ? _printed + "\n\n" + notice : notice;
    }

    /// <summary>Prints the terminal body, closes the line, and clears the diff base so the next turn starts clean.</summary>
    private async Task FinishAsync(string body)
    {
        await WriteBodyAsync(body).ConfigureAwait(false);
        await writer.WriteAsync('\n').ConfigureAwait(false);
        _printed = string.Empty;
    }

    /// <summary>Emits only the part of <paramref name="text"/> that is not on screen yet, and remembers all of it.</summary>
    private async Task WriteBodyAsync(string text)
    {
        if (text.StartsWith(_printed, StringComparison.Ordinal))
        {
            await writer.WriteAsync(text[_printed.Length..]).ConfigureAwait(false);
        }
        else
        {
            // A notice or a re-render that is not an extension of what we printed: start a fresh line.
            await writer.WriteAsync("\n" + text).ConfigureAwait(false);
        }

        _printed = text;
    }
}
