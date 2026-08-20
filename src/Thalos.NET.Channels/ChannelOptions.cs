namespace Thalos.Channels;

/// <summary>Options for channel hosting, bound from <see cref="SectionName"/>.</summary>
public sealed class ChannelOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Thalos:Channels";

    /// <summary>Runtime switch. When false the pump starts and immediately idles, so hosts can bind it from late configuration.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Agent used when a conversation is bound implicitly or by a bare <c>/new</c>.</summary>
    public string DefaultAgent { get; set; } = string.Empty;

    /// <summary>How long a conversation may sit idle before the next message rolls it onto a fresh session.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Minimum spacing between outbound renders of a running turn. Zero means render every delta, which is what the
    /// console does; Telegram sets a second to stay inside its per-chat rate budget.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The first violation as text, or null when the options are valid.</summary>
    public static string? Describe(ChannelOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);

        if (string.IsNullOrWhiteSpace(o.DefaultAgent))
        {
            return "DefaultAgent must not be blank.";
        }

        // Plain interpolation, not string.Create(CultureInfo.InvariantCulture, …): TimeSpan formats culture-invariantly,
        // so the wrapper is redundant and MA0185 rejects it. Skills uses string.Create because it interpolates a double.
        if (o.IdleTimeout <= TimeSpan.Zero)
        {
            return $"IdleTimeout must be greater than zero (was {o.IdleTimeout}).";
        }

        if (o.FlushInterval < TimeSpan.Zero)
        {
            return $"FlushInterval must not be negative (was {o.FlushInterval}).";
        }

        return null;
    }
}
