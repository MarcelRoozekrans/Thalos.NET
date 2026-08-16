using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Thalos.Runtime;

/// <summary>Turn-level tracing/metrics. Session-store spans come from ZeroAlloc.Telemetry's generated proxy.</summary>
public static class ThalosTelemetry
{
    /// <summary>Name of the <see cref="ActivitySource"/> and <see cref="Meter"/> (<c>"thalos"</c>) — subscribe to it in OpenTelemetry.</summary>
    public const string SourceName = "thalos";

    /// <summary>Emits the <c>thalos.turn</c> span (tags: <c>thalos.agent</c>, <c>thalos.session</c>, <c>thalos.turn</c>, token counts, <c>thalos.tool_calls</c>).</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>Owner of the counters/histograms below.</summary>
    public static readonly Meter Meter = new(SourceName);

    /// <summary><c>thalos.turns</c>: completed turns.</summary>
    public static readonly Counter<long> Turns = Meter.CreateCounter<long>("thalos.turns", description: "Completed turns");

    /// <summary><c>thalos.turn.failures</c>: failed turns, tagged <c>thalos.error</c> = <see cref="AgentErrorCode"/> name.</summary>
    public static readonly Counter<long> TurnFailures = Meter.CreateCounter<long>("thalos.turn.failures", description: "Failed turns, tagged by error code");

    /// <summary><c>thalos.turn.duration</c> (ms): wall-clock duration of completed turns.</summary>
    public static readonly Histogram<double> TurnDurationMs = Meter.CreateHistogram<double>("thalos.turn.duration", unit: "ms");

    /// <summary><c>thalos.tokens.input</c>: input tokens of completed turns (failed turns are not counted here; see <see cref="TurnFailedNotification.Usage"/>).</summary>
    public static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("thalos.tokens.input");

    /// <summary><c>thalos.tokens.output</c>: output tokens of completed turns.</summary>
    public static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>("thalos.tokens.output");
}
