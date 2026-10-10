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
}
