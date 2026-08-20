using System.Runtime.CompilerServices;
using ZeroAlloc.Authorization;

namespace Thalos.Channels.Console;

/// <summary>Reads one message per line. The reader is injected so hosts pass <c>System.Console.In</c> and tests pass a string.</summary>
public sealed class ConsoleChannelSource(TextReader reader, ISecurityContext caller) : IChannelSource
{
    /// <summary>The console conversation — there is only ever one.</summary>
    public static readonly ConversationId Conversation = new("console");

    /// <inheritdoc />
    public string ChannelId => "console";

    /// <inheritdoc />
    public async IAsyncEnumerable<InboundMessage> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return new InboundMessage(ChannelId, Conversation, line.Trim(), caller, null);
        }
    }
}
