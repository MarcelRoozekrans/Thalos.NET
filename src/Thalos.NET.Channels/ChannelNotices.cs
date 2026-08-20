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
}
