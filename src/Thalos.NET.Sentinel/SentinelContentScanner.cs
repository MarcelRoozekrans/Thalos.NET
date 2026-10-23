using AI.Sentinel;
using AI.Sentinel.Detection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Thalos.Runtime;

namespace Thalos.Sentinel;

/// <summary>
/// Runs untrusted text (recalled memories) through AI.Sentinel's detection pipeline — the same detectors that scan model
/// traffic — as a single user message, and quarantines it when any detection's effective action is
/// <see cref="SentinelAction.Quarantine"/>. The effective action is the one Sentinel itself applies,
/// <see cref="SentinelOptions.EffectiveAction(DetectionResult)"/>: a detector's own <see cref="DetectorConfiguration.Action"/>
/// when set, otherwise the severity mapping <see cref="SentinelOptions.OnCritical"/> … <see cref="SentinelOptions.OnLow"/>.
/// A redacting detector (PII, credentials) cannot redact stored text, so its finding falls back to the severity mapping, as
/// Sentinel does whenever redaction is not possible. The verdict detail is <c>"{Severity}: {DetectorId}"</c> of the most severe
/// quarantining detection; the detector's reason text goes to the log only.
/// </summary>
/// <remarks>
/// This runs the detection pipeline directly and bypasses Sentinel's InterventionEngine: non-quarantine outcomes (Log/Alert)
/// produce no Sentinel audit entry or alert here — only the 401 warning below when quarantined. That log carries the severity,
/// the detector id and the detector-authored <c>Reason</c> (a description of the detection, e.g. a similarity score), never the
/// scanned text; a test asserts the log line does not echo the content.
/// </remarks>
internal sealed partial class SentinelContentScanner(IDetectionPipeline pipeline, SentinelOptions options, ILogger<SentinelContentScanner>? logger = null) : IUntrustedContentScanner
{
    public async ValueTask<UntrustedContentVerdict> ScanAsync(string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return UntrustedContentVerdict.Allow();
        }

        var sessionId = TurnScope.Current?.SessionId.ToString() ?? "thalos-untrusted-content";
        var context = new SentinelContext(options.DefaultSenderId, options.DefaultReceiverId, new AI.Sentinel.Domain.SessionId(sessionId), [new ChatMessage(ChatRole.User, content)], []);
        var result = await pipeline.RunAsync(context, ct).ConfigureAwait(false);
        if (result.IsClean)
        {
            return UntrustedContentVerdict.Allow();
        }

        // Same choice as Sentinel's own pipeline: the most severe detection among those whose effective action is the
        // decisive one. Quarantine ranks above every other action, so any quarantining detection decides.
        var top = result.Detections.Where(d => !d.IsClean && options.EffectiveAction(d) == SentinelAction.Quarantine).MaxBy(d => d.Severity);
        if (top is null)
        {
            return UntrustedContentVerdict.Allow();
        }

        if (logger is not null)
        {
            LogQuarantined(logger, top.Severity, top.DetectorId.Value, top.Reason);
        }

        return UntrustedContentVerdict.Quarantine($"{top.Severity}: {top.DetectorId.Value}");
    }

    [LoggerMessage(EventId = 401, Level = LogLevel.Warning, Message = "AI.Sentinel quarantined untrusted content: {Severity} {Detector}: {Reason}")]
    private static partial void LogQuarantined(ILogger logger, Severity severity, string detector, string reason);
}
