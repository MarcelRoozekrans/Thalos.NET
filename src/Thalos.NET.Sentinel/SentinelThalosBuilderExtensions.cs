using AI.Sentinel;
using Microsoft.Extensions.DependencyInjection;

namespace Thalos.Sentinel;

public static class SentinelThalosBuilderExtensions
{
    /// <summary>
    /// Adds AI.Sentinel scanning to every agent. Tool-call authorization is enforced by Thalos itself
    /// (<c>RequireToolPolicy</c>) — Sentinel's <c>UseToolCallAuthorization</c> is intentionally not used (see design §0.1).
    /// </summary>
    /// <remarks>
    /// AI.Sentinel 2.0.1's security detectors (prompt injection, jailbreak, exfiltration, …) are embedding-based: set
    /// <see cref="SentinelOptions.EmbeddingGenerator"/> in <paramref name="configure"/>, otherwise they return Clean and only
    /// the lexical/operational detectors are active (Sentinel logs a warning when the first agent pipeline is built).
    /// </remarks>
    public static ThalosBuilder UseAISentinel(this ThalosBuilder builder, Action<SentinelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddAISentinel(configure);
        return builder.AddChatClientDecorator<SentinelChatClientDecorator>();
    }
}
