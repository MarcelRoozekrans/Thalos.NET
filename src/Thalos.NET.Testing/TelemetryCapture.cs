using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Thalos.Testing;

/// <summary>
/// Captures every span and every measurement from the <see cref="ActivitySource"/>s and <see cref="Meter"/>s named at
/// construction, until disposed. For asserting on telemetry without an OpenTelemetry SDK.
/// </summary>
/// <remarks>
/// <para>
/// It listens process-wide, so it also sees what other tests running in parallel emit on the same names. Filter on
/// something only your test produces: a fresh run id, or a node, process, tool or model name no other test uses. Never
/// assert on an unfiltered count.
/// </para>
/// <para>
/// Every span from a named source is sampled with all data, so a listener-dependent code path takes the "something is
/// listening" branch while the capture lives. Measurements of type <see cref="long"/>, <see cref="int"/> and
/// <see cref="double"/> are recorded, with a copy of their tags.
/// </para>
/// </remarks>
public sealed class TelemetryCapture : IDisposable
{
    private readonly HashSet<string> _names;
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters;
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ConcurrentQueue<CapturedMeasurement> _measurements = new();

    /// <summary>Starts capturing from the sources and meters named <paramref name="names"/>.</summary>
    /// <param name="names">Source and meter names, compared ordinally.</param>
    public TelemetryCapture(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _names = new HashSet<string>(names, StringComparer.Ordinal);

        _activities = new ActivityListener
        {
            ShouldListenTo = source => _names.Contains(source.Name),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _spans.Enqueue,
        };
        ActivitySource.AddActivityListener(_activities);

        _meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (_names.Contains(instrument.Meter.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.Start();
    }

    /// <summary>Every span stopped so far, in stop order.</summary>
    public IReadOnlyList<Activity> Spans => [.. _spans];

    /// <summary>Every measurement recorded so far, in record order.</summary>
    public IReadOnlyList<CapturedMeasurement> Measurements => [.. _measurements];

    /// <summary>The stopped spans whose <see cref="Activity.OperationName"/> is <paramref name="operationName"/>.</summary>
    public IReadOnlyList<Activity> SpansNamed(string operationName) =>
        [.. _spans.Where(s => string.Equals(s.OperationName, operationName, StringComparison.Ordinal))];

    /// <summary>The measurements of the instrument named <paramref name="instrument"/>.</summary>
    public IReadOnlyList<CapturedMeasurement> MeasurementsOf(string instrument) =>
        [.. _measurements.Where(m => string.Equals(m.Instrument, instrument, StringComparison.Ordinal))];

    /// <inheritdoc/>
    public void Dispose()
    {
        _activities.Dispose();
        _meters.Dispose();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        _measurements.Enqueue(new CapturedMeasurement(instrument.Name, value, copy));
    }
}

/// <summary>One measurement a <see cref="TelemetryCapture"/> saw.</summary>
/// <param name="Instrument">The instrument's name.</param>
/// <param name="Value">The measured value, widened to <see cref="double"/>.</param>
/// <param name="Tags">A copy of the measurement's tags.</param>
public sealed record CapturedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);
