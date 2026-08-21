namespace Thalos.Channels.Console;

/// <summary>
/// Writes turn output to a <see cref="TextWriter"/>. The pump renders cumulatively, so this adapter prints only the
/// suffix it has not printed yet — a terminal cannot edit what it already emitted.
/// </summary>
/// <remarks>
/// The conversation id is ignored: a console has exactly one, and there is only one stream to write it to.
/// </remarks>
public sealed class ConsoleChannelAdapter(TextWriter writer) : IChannelAdapter
{
    private string _printed = string.Empty;

    /// <inheritdoc />
    public string ChannelId => "console";

    /// <inheritdoc />
    public async ValueTask DeliverAsync(ConversationId conversationId, AgentEvent agentEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        switch (agentEvent)
        {
            case TextDeltaEvent delta:
                if (delta.Text.StartsWith(_printed, StringComparison.Ordinal))
                {
                    await writer.WriteAsync(delta.Text[_printed.Length..]).ConfigureAwait(false);
                }
                else
                {
                    // A notice or a re-render that is not an extension of what we printed: start a fresh line.
                    await writer.WriteAsync("\n" + delta.Text).ConfigureAwait(false);
                }

                _printed = delta.Text;
                break;

            case TurnCompletedEvent or TurnFailedEvent:
                await writer.WriteAsync('\n').ConfigureAwait(false);
                _printed = string.Empty;
                break;

            default:
                break;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }
}
