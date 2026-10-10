using System.Diagnostics;
using Thalos;
using Thalos.Skills;
using Thalos.Testing;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// 0.15.0, spec Part A step 4: a host action call is a <c>thalos.workflow.host_action.{type}</c> span under its dispatch,
/// tagged with its run, node and action, and timed on <c>thalos.workflow.host_action.duration</c>. A failed
/// <c>Result</c> marks the span as an error. The dispatcher wraps the actions a host passes in, so no host has to.
/// The action and node names here, <c>ha-*</c>, are this class's own.
/// </summary>
public sealed class HostActionTelemetryTests
{
    private const string Action = "ha-action";
    // A literal on purpose: deriving it from the constant would let a change to the constant move the test with it.
    private const string SpanName = "thalos.workflow.host_action.RecordingAction";

    private const string Yaml = """
        process: ha-process
        version: 1
        nodes:
          ha-publish:
            action: ha-action
            outcomes: [published, failed]
            branch: { published: ha-done, failed: ha-stop }
          ha-done: { terminal: succeeded }
          ha-stop: { terminal: failed }
        """;

    /// <summary>
    /// Red: drop the wrapping in the dispatcher's <c>_hostActions</c> initializer; no host-action span exists.
    /// Red: drop the <c>{type}</c> token from the span name; the name assertion fails.
    /// Red: drop either <c>[TraceTag]</c> on <c>run</c>; its assertion fails.
    /// Red: drop the <c>[TraceTag]</c> on <c>node</c>; the action assertion fails.
    /// Red: start the dispatch span after the action runs; the parent assertion fails.
    /// </summary>
    [Fact]
    public async Task A_host_action_is_a_span_under_its_dispatch_tagged_with_run_node_and_action()
    {
        var (dispatcher, message) = await ArrangeAsync(RecordingAction.Returning(Action, new HostActionResult("published", Vars())));
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        var span = capture.SpansNamed(SpanName).Should().ContainSingle(s => message.RunId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag))).Subject;
        span.GetTagItem(WorkflowTelemetry.NodeTag).Should().Be("ha-publish");
        span.GetTagItem(WorkflowTelemetry.HostActionTag).Should().Be(Action);
        span.Status.Should().Be(ActivityStatusCode.Unset);
        var dispatch = capture.SpansNamed(WorkflowTelemetry.DispatchSpanName).Single(s => message.RunId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag)));
        span.ParentSpanId.Should().Be(dispatch.SpanId);
    }

    /// <summary>
    /// Red: drop <c>ErrorWhen = "IsFailure"</c>; the span's status is Unset.
    /// Red: add <c>ErrorDescription = "Error"</c>; the description is the action's message.
    /// </summary>
    [Fact]
    public async Task A_failed_result_marks_the_span_as_an_error_with_no_description()
    {
        var (dispatcher, message) = await ArrangeAsync(RecordingAction.Failing(Action, "push failed; worktree kept at C:/w"));
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        var span = capture.SpansNamed(SpanName).Single(s => message.RunId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag)));
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().BeNull();
    }

    /// <summary>
    /// Red: drop <c>ExceptionDescription = false</c>; the description is "boom".
    /// Red: drop the <c>[Trace]</c> on <c>RunAsync</c>, with its <c>[TraceTag]</c>s since ZTEL004 refuses them alone; no
    /// host-action span exists, so the <c>Single</c> span lookup throws before the <c>error.type</c> assertion.
    /// </summary>
    [Fact]
    public async Task An_exception_marks_the_span_with_its_type_and_no_message()
    {
        var (dispatcher, message) = await ArrangeAsync(RecordingAction.Throwing(Action, new InvalidOperationException("boom")));
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        var dispatch = async () => await dispatcher.DispatchAsync(message, CancellationToken.None);
        await dispatch.Should().ThrowAsync<InvalidOperationException>();

        var span = capture.SpansNamed(SpanName).Single(s => message.RunId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag)));
        span.GetTagItem(WorkflowTelemetry.ErrorTypeTag).Should().Be(typeof(InvalidOperationException).FullName);
        span.StatusDescription.Should().BeNull();
    }

    /// <summary>
    /// Red: drop the <c>[Histogram]</c> on <c>RunAsync</c>, with its <c>[MetricTag]</c> since ZTEL011 refuses it alone; no
    /// measurement exists.
    /// Red: drop the <c>[MetricTag]</c> on <c>node</c>; the Single finds no measurement tagged with the action.
    /// Red: change the histogram's <c>Unit</c> to <c>"s"</c>; the value is under 1 and the assertion fails.
    /// </summary>
    [Fact]
    public async Task The_host_action_duration_is_recorded_by_action()
    {
        var slow = new RecordingAction(Action, async ct =>
        {
            await Task.Delay(50, ct);
            return Result<HostActionResult>.Success(new HostActionResult("published", Vars()));
        });
        var (dispatcher, message) = await ArrangeAsync(slow);
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        capture.MeasurementsOf(WorkflowTelemetry.HostActionDurationName)
            .Single(m => Action.Equals(m.Tags.GetValueOrDefault(WorkflowTelemetry.HostActionTag)))
            .Value.Should().BeGreaterThanOrEqualTo(40);
    }

    private static async Task<(WorkflowNodeDispatcher Dispatcher, WorkflowDispatchMessage Message)> ArrangeAsync(IWorkflowHostAction action)
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(Yaml);
        var store = new FakeWorkflowStore(definitions);
        var dispatcher = new WorkflowNodeDispatcher(
            store, new FakeSubagentRunner(), new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal)),
            definitions, new InMemorySkillStore(TimeProvider.System), _ => new FakeSecurityContext("workflow-engine"), [], [action]);
        var runId = (await store.StartAsync(new WorkflowStartRequest
        {
            Process = "ha-process",
            Version = 1,
            CorrelationKey = $"c-{Guid.NewGuid():N}",
            StartNode = "ha-publish",
            InitialVariables = null,
            StartedBy = TestPrincipals.Starter,
        }, CancellationToken.None)).Value;
        return (dispatcher, store.TakeNext(runId)!);
    }

    private static Dictionary<string, object?> Vars() => new(StringComparer.Ordinal);
}
