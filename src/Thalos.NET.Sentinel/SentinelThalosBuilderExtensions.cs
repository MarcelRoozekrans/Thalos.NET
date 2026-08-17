using AI.Sentinel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Thalos.Sentinel;

/// <summary>Registers AI.Sentinel as a Thalos chat-client decorator and as the <see cref="IUntrustedContentScanner"/>.</summary>
public static class SentinelThalosBuilderExtensions
{
    /// <summary>
    /// Adds AI.Sentinel scanning to every agent. Tool-call authorization is enforced by Thalos itself
    /// (<c>RequireToolPolicy</c>) — Sentinel's <c>UseToolCallAuthorization</c> is intentionally not used (see design §0.1).
    /// Also registers <see cref="IUntrustedContentScanner"/> (over the same detection pipeline) so Thalos.NET.Memory scans
    /// recalled memories before injecting them. Calling this twice is a no-op (the first configuration wins).
    /// </summary>
    /// <remarks>
    /// AI.Sentinel 2.0.1's security detectors (prompt injection, jailbreak, exfiltration, …) are embedding-based: set
    /// <see cref="SentinelOptions.EmbeddingGenerator"/> in <paramref name="configure"/>, otherwise they return Clean and only
    /// the lexical/operational detectors are active (Sentinel warns per agent pipeline when it is first built).
    /// </remarks>
    public static ThalosBuilder UseAISentinel(this ThalosBuilder builder, Action<SentinelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // AddAISentinel is not idempotent (it AddSingleton()s SentinelOptions, the audit store, the alert sink, …); a second call would
        // register a second pipeline and duplicate detectors, so guard on the SentinelOptions registration it makes.
        if (builder.Services.Any(d => d.ServiceType == typeof(SentinelOptions)))
        {
            return builder;
        }

        builder.Services.AddAISentinel(configure);
        builder.Services.TryAddSingleton<IUntrustedContentScanner, SentinelContentScanner>();
        return builder.AddChatClientDecorator<SentinelChatClientDecorator>();
    }
}
