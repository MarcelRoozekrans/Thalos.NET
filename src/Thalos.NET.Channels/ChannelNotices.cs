namespace Thalos.Channels;

/// <summary>Operator-facing copy. Centralised so every channel says the same thing and the tests can assert on it.</summary>
public static class ChannelNotices
{
    /// <summary>Shown when an idle conversation rolls onto a fresh session.</summary>
    public const string IdleRollover = "That conversation was idle, so I started a new session. Earlier context is gone.";

    /// <summary>
    /// Shown when the bound session no longer exists. The binding is cleared so the next message starts a fresh
    /// session; this message is not auto-retried, so the copy must ask for it back rather than implying it ran.
    /// </summary>
    public const string Rebound = "That session had already ended, so I cleared it. Send that message again and I will start a new session.";

    /// <summary>Shown when a turn is already running.</summary>
    public const string Busy = "Still working on the previous message — /cancel to stop it.";

    /// <summary>Shown when <c>Thalos:Channels:DefaultAgent</c> names an agent the catalogue does not have.</summary>
    public const string UnknownDefaultAgent = "I am misconfigured: the default agent does not exist. /agents lists what is registered.";

    /// <summary>Shown when /new names an agent the catalogue does not have.</summary>
    public const string UnknownAgent = "I do not have an agent by that name. /agents lists the ones I do.";

    /// <summary>Shown for /status when nothing is bound.</summary>
    public const string NoSession = "No active session. Send a message, or /new to start one.";

    /// <summary>Shown for a slash-prefixed word that is not a command.</summary>
    public const string UnknownCommand = "I do not know that command. /help lists the ones I do.";

    /// <summary>The /help body.</summary>
    public const string Help =
        "/new [agent] — start a fresh session\n" +
        "/end — close the current session\n" +
        "/status — what session am I in\n" +
        "/agents — list available agents\n" +
        "/cancel — stop the running turn\n" +
        "/help — this list";

    /// <summary>Shown for /cancel when no turn is running.</summary>
    public const string NothingToCancel = "Nothing is running.";

    /// <summary>Shown after /cancel actually stopped a running turn.</summary>
    public const string Cancelled = "Cancelled.";

    /// <summary>Shown after /end closes the bound session.</summary>
    public const string SessionEnded = "Session ended.";
}
