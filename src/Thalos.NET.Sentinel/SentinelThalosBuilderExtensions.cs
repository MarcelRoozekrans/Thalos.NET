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
    /// AI.Sentinel 3.x's security detectors are mostly semantic: without <see cref="SentinelOptions.EmbeddingGenerator"/>
    /// set in <paramref name="configure"/> they return Clean and only the lexical/operational detectors are active
    /// (Sentinel warns per agent pipeline when it is first built). SEC-01 PromptInjection and SEC-05 Jailbreak are the
    /// exception — both carry a rule layer that catches unambiguous phrasings with no generator configured; only
    /// paraphrases need one. Set <see cref="SentinelOptions.EmbeddingGenerator"/> for full semantic coverage across
    /// every detector, including paraphrased SEC-01/SEC-05 attempts.
    /// <para>
    /// AI.Sentinel 3.x defaults apply unchanged. In particular the PII and credential detectors redact by default: an email
    /// address or an API key in a prompt reaches the model as a placeholder and the turn succeeds, instead of taking the
    /// severity action. Set <see cref="SentinelOptions.RedactByDefault"/> to <see langword="false"/> in
    /// <paramref name="configure"/> to give their findings the severity action again. Recalled memories cannot be redacted, so
    /// the <see cref="IUntrustedContentScanner"/> always gives those findings their severity action. Since 3.0 the PII detector
    /// matches every email address at Medium severity, so with <see cref="SentinelOptions.OnMedium"/> set to
    /// <see cref="SentinelAction.Quarantine"/> any recalled memory that contains an email address is quarantined, whatever
    /// <see cref="SentinelOptions.RedactByDefault"/> says.
    /// </para>
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
