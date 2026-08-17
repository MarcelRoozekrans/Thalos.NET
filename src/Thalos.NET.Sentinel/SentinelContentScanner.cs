using AI.Sentinel;
using AI.Sentinel.Detection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Thalos.Runtime;

namespace Thalos.Sentinel;

/// <summary>
/// Runs untrusted text (recalled memories) through AI.Sentinel's detection pipeline — the same detectors that scan model
/// traffic — as a single user message, and quarantines it when the top detection's configured action
/// (<see cref="SentinelOptions.OnCritical"/> … <see cref="SentinelOptions.OnLow"/>) is <see cref="SentinelAction.Quarantine"/>.
/// The verdict detail is <c>"{Severity}: {DetectorId}"</c>; the detector's reason text goes to the log only.
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
        var context = new SentinelContext(options.DefaultSenderId, options.DefaultReceiverId, new AI.Sentinel.Domain.SessionId(sessionId), [new ChatMessage(ChatRole.User, content)], [], null);
        var result = await pipeline.RunAsync(context, ct).ConfigureAwait(false);
        if (result.IsClean)
        {
            return UntrustedContentVerdict.Allow();
        }

        var top = result.Detections.Where(d => !d.IsClean).MaxBy(d => d.Severity);
        if (top is null)
        {
            return UntrustedContentVerdict.Allow();
        }

        var action = top.Severity switch
        {
            Severity.Critical => options.OnCritical,
            Severity.High => options.OnHigh,
            Severity.Medium => options.OnMedium,
            Severity.Low => options.OnLow,
            _ => SentinelAction.PassThrough,
        };
        if (action != SentinelAction.Quarantine)
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
