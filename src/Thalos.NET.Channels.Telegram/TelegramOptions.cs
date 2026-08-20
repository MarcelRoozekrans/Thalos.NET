using System.Globalization;
using ZeroAlloc.Authorization;

namespace Thalos.Channels.Telegram;

/// <summary>Options for the Telegram channel, bound from <see cref="SectionName"/>.</summary>
public sealed class TelegramOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Thalos:Channels:Telegram";

    /// <summary>Runtime switch. When false the channel is not registered as a pumped source.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The bot token issued by <c>@BotFather</c>, used to authenticate every Bot API call.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// The Telegram user ids permitted to talk to the bot. A message from any other sender is dropped before it
    /// ever becomes an <see cref="InboundMessage"/> — silently, with no reply — because a chat that reaches this
    /// far is one step from an AI agent with tools. An empty list is <em>not</em> "allow everyone": it is the one
    /// misconfiguration that would expose the agent to anyone who finds the bot, so <see cref="Describe"/> rejects
    /// it as a validation failure rather than treating it as a permissive default.
    /// </summary>
    public IList<long> AllowedUserIds { get; set; } = [];

    /// <summary>
    /// The caller id given to every accepted message's <see cref="ISecurityContext"/> (e.g. <c>telegram:marcel</c>).
    /// Every Telegram sender who passes <see cref="AllowedUserIds"/> is attributed to this one configured principal
    /// — Telegram's own per-sender identity is not carried through, on the same basis as the console channel.
    /// </summary>
    public string PrincipalId { get; set; } = string.Empty;

    /// <summary>The roles given to every accepted message's <see cref="ISecurityContext"/>.</summary>
    public IList<string> Roles { get; set; } = [];

    /// <summary>How many seconds Telegram should hold a <c>getUpdates</c> long-poll open waiting for the next update.</summary>
    public int PollTimeoutSeconds { get; set; } = 50;

    /// <summary>The first violation as text, or null when the options are valid.</summary>
    public static string? Describe(TelegramOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);

        if (string.IsNullOrWhiteSpace(o.BotToken))
        {
            return "BotToken must not be blank.";
        }

        if (string.IsNullOrWhiteSpace(o.PrincipalId))
        {
            return "PrincipalId must not be blank.";
        }

        if (o.AllowedUserIds.Count == 0)
        {
            return "AllowedUserIds must not be empty — an empty allow-list would let anyone who finds the bot reach the agent, not lock it down.";
        }

        if (o.PollTimeoutSeconds <= 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"PollTimeoutSeconds must be greater than zero (was {o.PollTimeoutSeconds}).");
        }

        return null;
    }
}
