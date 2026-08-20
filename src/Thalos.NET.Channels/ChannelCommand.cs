namespace Thalos.Channels;

/// <summary>What an inbound message asked the pump to do.</summary>
public enum ChannelCommandKind
{
    /// <summary>Not a command — run it as a turn.</summary>
    None = 0,

    /// <summary>Start a fresh session, optionally naming an agent.</summary>
    New,

    /// <summary>Close the bound session.</summary>
    End,

    /// <summary>Report the bound session.</summary>
    Status,

    /// <summary>List available agents.</summary>
    Agents,

    /// <summary>Abort the in-flight turn.</summary>
    Cancel,

    /// <summary>List the commands.</summary>
    Help,

    /// <summary>Slash-prefixed but not recognised.</summary>
    Unknown,
}

/// <summary>A parsed channel command. <see cref="Argument"/> is the remainder of the line, or null when there is none.</summary>
public sealed record ChannelCommand(ChannelCommandKind Kind, string? Argument)
{
    private static readonly ChannelCommand NotACommand = new(ChannelCommandKind.None, null);

    /// <summary>
    /// Parses <paramref name="text"/>. Anything not starting with <c>/</c> is <see cref="ChannelCommandKind.None"/>;
    /// a slash-prefixed word that is not recognised is <see cref="ChannelCommandKind.Unknown"/> rather than text, so
    /// a mistyped command is never forwarded to the model as a prompt.
    /// </summary>
    public static ChannelCommand Parse(string text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '/')
        {
            return NotACommand;
        }

        var separator = trimmed.IndexOf(' ');
        var word = separator < 0 ? trimmed[1..] : trimmed[1..separator];
        var argument = separator < 0 ? null : trimmed[(separator + 1)..].Trim();

        // Telegram appends @botname to commands; strip it before matching.
        var at = word.IndexOf('@');
        if (at >= 0)
        {
            word = word[..at];
        }

        var kind = word.ToLowerInvariant() switch
        {
            "new" => ChannelCommandKind.New,
            "end" => ChannelCommandKind.End,
            "status" => ChannelCommandKind.Status,
            "agents" => ChannelCommandKind.Agents,
            "cancel" => ChannelCommandKind.Cancel,
            "help" => ChannelCommandKind.Help,
            _ => ChannelCommandKind.Unknown,
        };

        return new ChannelCommand(kind, string.IsNullOrEmpty(argument) ? null : argument);
    }
}
