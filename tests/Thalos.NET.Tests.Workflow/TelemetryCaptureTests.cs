using System.Diagnostics;
using System.Diagnostics.Metrics;
using Thalos.Testing;

namespace Thalos.Tests.Workflow;

/// <summary>
/// The shared fixture every telemetry test in Thalos and Daedalus reads through. It must see the sources it names and
/// nothing else, or a test could pass on another test's span.
/// </summary>
public sealed class TelemetryCaptureTests
{
    /// <summary>
    /// Red: make <c>ShouldListenTo</c> return true for every source; the other source's span is captured.
    /// Red: enable every instrument in <c>InstrumentPublished</c>; the other meter's measurement is captured.
    /// Red: record <c>0</c> instead of the measurement; the value assertion fails.
    /// Red: drop the tag copy in <c>Record</c>; the tag assertion fails.
    /// </summary>
    [Fact]
    public void It_captures_spans_and_measurements_from_the_named_sources_only()
    {
        var name = $"probe.{Guid.NewGuid():N}";
        var other = $"probe.{Guid.NewGuid():N}";
        using var capture = new TelemetryCapture(name);
        using var source = new ActivitySource(name);
        using var otherSource = new ActivitySource(other);
        using var meter = new Meter(name);
        using var otherMeter = new Meter(other);

        source.StartActivity("probe.span")?.Dispose();
        otherSource.StartActivity("probe.other")?.Dispose();
        meter.CreateCounter<long>("probe.count").Add(3, new KeyValuePair<string, object?>("k", "v"));
        otherMeter.CreateCounter<long>("probe.other").Add(1);

        capture.Spans.Should().ContainSingle().Which.OperationName.Should().Be("probe.span");
        var measurement = capture.Measurements.Should().ContainSingle().Subject;
        measurement.Instrument.Should().Be("probe.count");
        measurement.Value.Should().Be(3);
        measurement.Tags.Should().Contain("k", "v");
    }

    /// <summary>
    /// Red: delete <c>SetMeasurementEventCallback&lt;double&gt;</c>; no measurement is captured and the single-item assertion fails.
    /// Red: record <c>0</c> in that callback; the value assertion fails.
    /// </summary>
    [Fact]
    public void It_captures_double_measurements()
    {
        var name = $"probe.{Guid.NewGuid():N}";
        using var capture = new TelemetryCapture(name);
        using var meter = new Meter(name);

        meter.CreateHistogram<double>("probe.duration").Record(12.5);

        var measurement = capture.Measurements.Should().ContainSingle().Subject;
        measurement.Instrument.Should().Be("probe.duration");
        measurement.Value.Should().Be(12.5);
    }

    /// <summary>
    /// Red: invert the filter in <c>SpansNamed</c>; it returns the other span.
    /// Red: invert the filter in <c>MeasurementsOf</c>; it returns the other instrument's measurement.
    /// </summary>
    [Fact]
    public void SpansNamed_and_MeasurementsOf_filter_by_name()
    {
        var name = $"probe.{Guid.NewGuid():N}";
        using var capture = new TelemetryCapture(name);
        using var source = new ActivitySource(name);
        using var meter = new Meter(name);
        source.StartActivity("probe.one")?.Dispose();
        source.StartActivity("probe.two")?.Dispose();
        meter.CreateCounter<long>("probe.a").Add(1);
        meter.CreateCounter<long>("probe.b").Add(2);

        capture.Spans.Should().HaveCount(2);
        capture.Measurements.Should().HaveCount(2);
        capture.SpansNamed("probe.one").Should().ContainSingle().Which.OperationName.Should().Be("probe.one");
        capture.SpansNamed("probe.missing").Should().BeEmpty();
        capture.MeasurementsOf("probe.b").Should().ContainSingle().Which.Value.Should().Be(2);
        capture.MeasurementsOf("probe.missing").Should().BeEmpty();
    }

    /// <summary>
    /// Red: enqueue at <c>ActivityStarted</c> in place of <c>ActivityStopped</c>; the running span is already captured and the first assertion fails.
    /// Red: never enqueue; the stopped-span assertions fail.
    /// </summary>
    [Fact]
    public void A_span_is_captured_when_it_stops_with_its_final_tags_and_status()
    {
        var name = $"probe.{Guid.NewGuid():N}";
        using var capture = new TelemetryCapture(name);
        using var source = new ActivitySource(name);

        var activity = source.StartActivity("probe.span");
        activity.Should().NotBeNull();
        capture.Spans.Should().BeEmpty();

        activity!.SetTag("k", "v");
        activity.SetStatus(ActivityStatusCode.Error);
        activity.Dispose();

        var span = capture.Spans.Should().ContainSingle().Subject;
        span.GetTagItem("k").Should().Be("v");
        span.Status.Should().Be(ActivityStatusCode.Error);
    }

    /// <summary>
    /// Red: do not dispose the activity listener in <c>Dispose</c>; the span after disposal is captured.
    /// Red: do not dispose the meter listener in <c>Dispose</c>; the measurement after disposal is captured.
    /// </summary>
    [Fact]
    public void Nothing_is_captured_after_Dispose()
    {
        var name = $"probe.{Guid.NewGuid():N}";
        var capture = new TelemetryCapture(name);
        using var source = new ActivitySource(name);
        using var meter = new Meter(name);
        var counter = meter.CreateCounter<long>("probe.count");
        source.StartActivity("probe.before")?.Dispose();
        counter.Add(1);
        capture.Spans.Should().ContainSingle();
        capture.Measurements.Should().ContainSingle();

        capture.Dispose();
        source.StartActivity("probe.after")?.Dispose();
        counter.Add(1);

        capture.Spans.Should().ContainSingle();
        capture.Measurements.Should().ContainSingle();
    }
}
