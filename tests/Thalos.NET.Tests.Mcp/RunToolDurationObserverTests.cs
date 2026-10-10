using Microsoft.Extensions.DependencyInjection;
using Thalos.Mcp;
using Thalos.Runtime;
using Thalos.Testing;
using Thalos.Workspaces;

namespace Thalos.Tests.Mcp;

/// <summary>
/// 0.15.0, spec Part A step 5: a run tool call, sandbox build and test included, is timed on
/// <c>thalos.workflow.run_tool.duration</c> from <see cref="RunToolCall.Elapsed"/>. The observer runs after the call
/// has finished, so the duration is the honest signal; a span there would be invented.
/// </summary>
public sealed class RunToolDurationObserverTests
{
    /// <summary>
    /// Red: record <c>Elapsed.TotalSeconds</c>; the value is 0.042.
    /// Red: tag the call's <c>ResultText</c>; the exact-tags assertion fails.
    /// Red: create the meter under <c>ThalosTelemetry.SourceName</c>; the capture on <c>thalos.workflow</c> sees nothing.
    /// </summary>
    [Fact]
    public async Task A_completed_call_is_timed_by_source_and_tool()
    {
        var tool = $"probe_tool_{Guid.NewGuid():N}";
        using var capture = new TelemetryCapture(ThalosTelemetry.WorkflowSourceName);

        await new RunToolDurationObserver().OnCompletedAsync(
            new RunToolCall(Guid.NewGuid(), "sandbox", tool, TestCallers.RunCaller(Guid.NewGuid()), "zqx result text", TimeSpan.FromMilliseconds(42)),
            CancellationToken.None);

        var measurement = capture.MeasurementsOf("thalos.workflow.run_tool.duration")
            .Should().ContainSingle(m => tool.Equals(m.Tags.GetValueOrDefault("thalos.tool"))).Subject;
        measurement.Value.Should().Be(42);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>(StringComparer.Ordinal) { ["thalos.tool.source"] = "sandbox", ["thalos.tool"] = tool });
    }

    /// <summary>
    /// Red: drop the registration from <c>RegisterRemote</c>; the count is 0.
    /// Red: register it with <c>AddSingleton</c> in place of <c>TryAddEnumerable</c>; two sources register it twice.
    /// </summary>
    [Fact]
    public void Every_remote_run_tool_source_shares_one_registered_observer()
    {
        var services = new ServiceCollection();

        McpThalosBuilderExtensions.AddRemote(services, "probe-a", _ => throw new InvalidOperationException("not built here"));
        McpThalosBuilderExtensions.AddRemote(services, "probe-b", _ => throw new InvalidOperationException("not built here"));

        services.Count(d => d.ServiceType == typeof(IRunToolCallObserver) && d.ImplementationType == typeof(RunToolDurationObserver))
            .Should().Be(1);
    }

    /// <summary>
    /// Red: register it with <c>AddSingleton</c> in place of <c>TryAddEnumerable</c>; two sources notify two observers
    /// and the call is measured twice.
    /// </summary>
    [Fact]
    public async Task Two_remote_sources_still_measure_a_call_once()
    {
        var tool = $"probe_tool_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        McpThalosBuilderExtensions.AddRemote(services, "probe-a", _ => throw new InvalidOperationException("not built here"));
        McpThalosBuilderExtensions.AddRemote(services, "probe-b", _ => throw new InvalidOperationException("not built here"));
        await using var provider = services.BuildServiceProvider();
        using var capture = new TelemetryCapture(ThalosTelemetry.WorkflowSourceName);
        var call = new RunToolCall(Guid.NewGuid(), "probe-b", tool, TestCallers.RunCaller(Guid.NewGuid()), "zqx", TimeSpan.FromMilliseconds(7));

        foreach (var observer in provider.GetServices<IRunToolCallObserver>())
        {
            await observer.OnCompletedAsync(call, CancellationToken.None);
        }

        capture.MeasurementsOf("thalos.workflow.run_tool.duration")
            .Count(m => tool.Equals(m.Tags.GetValueOrDefault("thalos.tool")))
            .Should().Be(1);
    }
}
