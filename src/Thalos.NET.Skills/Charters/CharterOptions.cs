namespace Thalos.Skills.Charters;

/// <summary>Host-wide chartered-agent configuration (section <c>Thalos:Charters</c>).</summary>
/// <remarks>
/// Only <see cref="Roots"/> is configuration-bindable — <c>UseRoleCharters(IConfiguration)</c> binds it from the
/// <c>Thalos:Charters</c> section. <see cref="Envelopes"/> carries a typed <see cref="AgentId"/>, which
/// <c>ThalosOptions</c> already documents as not configuration-bindable, so envelopes are always added in code, via
/// <c>UseRoleCharters(Action&lt;CharterOptions&gt;)</c>.
/// </remarks>
public sealed class CharterOptions
{
    /// <summary>Configuration section bound by <c>UseRoleCharters(IConfiguration)</c>.</summary>
    public const string SectionName = "Thalos:Charters";

    /// <summary>Folders scanned for <c>&lt;role&gt;/CHARTER.md</c> and <c>&lt;role&gt;.md</c>, in precedence order.</summary>
    public IList<string> Roots { get; set; } = [];

    /// <summary>The security envelope — identity and tools — for every chartered role. Added in code; never bound from configuration.</summary>
    public IList<AgentEnvelope> Envelopes { get; } = [];
}
