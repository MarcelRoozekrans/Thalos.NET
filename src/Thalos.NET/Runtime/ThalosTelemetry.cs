using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Thalos.Runtime;

/// <summary>Turn-level tracing/metrics. Session-store spans come from ZeroAlloc.Telemetry's generated proxy.</summary>
public static class ThalosTelemetry
{
    public const string SourceName = "thalos";
    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);
    public static readonly Counter<long> Turns = Meter.CreateCounter<long>("thalos.turns", description: "Completed turns");
    public static readonly Counter<long> TurnFailures = Meter.CreateCounter<long>("thalos.turn.failures", description: "Failed turns, tagged by error code");
    public static readonly Histogram<double> TurnDurationMs = Meter.CreateHistogram<double>("thalos.turn.duration", unit: "ms");
    public static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("thalos.tokens.input");
    public static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>("thalos.tokens.output");
}
