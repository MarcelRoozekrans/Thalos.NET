using System.Diagnostics;
using Thalos;
using Thalos.Skills;
using Thalos.Testing;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// 0.15.0, spec Part A steps 2 and 3: each node dispatch is a <c>thalos.workflow.dispatch</c> span of kind Consumer,
/// tagged with its run, node, seq and process and the outcome it ended with, linked to the run's starting context, and
/// timed on <c>thalos.workflow.dispatch.duration</c>. The node names here, <c>dt-*</c>, are this class's own, so the
/// histogram can be filtered on them while other tests run.
/// </summary>
public sealed class DispatchTelemetryTests
{
    private const string Action = "dt-action";

    private const string ActionYaml = """
        process: dt-process
        version: 1
        nodes:
          dt-publish:
            action: dt-action
            outcomes: [published, failed]
            branch: { published: dt-done, failed: dt-stop }
          dt-done: { terminal: succeeded }
          dt-stop: { terminal: failed }
        """;

    private const string GateYaml = """
        process: dt-gated
        version: 1
        nodes:
          dt-gate: { await: human_approval, next: dt-after }
          dt-after: { terminal: succeeded }
        """;

    /// <summary>
    /// Red: start the span with <c>ActivityKind.Internal</c>; the kind assertion fails.
    /// Red: drop any one of the four SetTag calls in <c>StartDispatchActivity</c>; its assertion fails.
    /// Red: map <c>DispatchOutcome.Completed</c> to <c>"failed"</c> in <c>OutcomeName</c>; the outcome assertion fails.
    /// Red: set Error status on every outcome; the status assertion fails.
    /// </summary>
    [Fact]
    public async Task A_dispatch_is_one_consumer_span_tagged_with_its_run_node_seq_process_and_outcome()
    {
        var arranged = await ArrangeAsync(ActionYaml, "dt-process", "dt-publish", RecordingAction.Returning(Action, new HostActionResult("published", Vars())));
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await arranged.Dispatcher.DispatchAsync(arranged.Message, CancellationToken.None);

        var span = DispatchOf(capture, arranged.Message.RunId);
        span.Kind.Should().Be(ActivityKind.Consumer);
        span.GetTagItem(WorkflowTelemetry.RunIdTag).Should().Be(arranged.Message.RunId);
        span.GetTagItem(WorkflowTelemetry.NodeTag).Should().Be("dt-publish");
        span.GetTagItem(WorkflowTelemetry.SeqTag).Should().Be(arranged.Message.Seq);
        span.GetTagItem(WorkflowTelemetry.ProcessTag).Should().Be("dt-process");
        span.GetTagItem(WorkflowTelemetry.OutcomeTag).Should().Be(WorkflowTelemetry.OutcomeCompleted);
        span.Status.Should().Be(ActivityStatusCode.Unset);
    }

    /// <summary>
    /// Red: return <c>Completed</c> from <c>FailRunAsync</c>; the outcome assertion fails.
    /// Red: do not set Error status on a failed outcome; the status assertion fails.
    /// Red: pass the run's <c>LastError</c> as the status description; the description assertion fails.
    /// </summary>
    [Fact]
    public async Task A_failed_node_ends_its_span_failed_with_error_status_and_no_description()
    {
        var arranged = await ArrangeAsync(ActionYaml, "dt-process", "dt-publish", RecordingAction.Failing(Action, "push failed; worktree kept at C:/w"));
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await arranged.Dispatcher.DispatchAsync(arranged.Message, CancellationToken.None);

        var span = DispatchOf(capture, arranged.Message.RunId);
        span.GetTagItem(WorkflowTelemetry.OutcomeTag).Should().Be(WorkflowTelemetry.OutcomeFailed);
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().BeNull();
    }

    /// <summary>Red: map <c>WorkflowStatus.Awaiting</c> to <c>Completed</c> in <c>AdvanceAndPersistAsync</c>; the outcome is "completed".</summary>
    [Fact]
    public async Task A_gate_dispatch_ends_awaiting()
    {
        var arranged = await ArrangeAsync(GateYaml, "dt-gated", "dt-gate");
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await arranged.Dispatcher.DispatchAsync(arranged.Message, CancellationToken.None);

        DispatchOf(capture, arranged.Message.RunId).GetTagItem(WorkflowTelemetry.OutcomeTag).Should().Be(WorkflowTelemetry.OutcomeAwaiting);
    }

    /// <summary>
    /// Red: start the span only after the stale check; no span exists for the message.
    /// Red: return <c>Failed</c> for a stale message; the outcome assertion fails.
    /// </summary>
    [Fact]
    public async Task A_stale_message_is_traced_as_skipped()
    {
        var arranged = await ArrangeAsync(ActionYaml, "dt-process", "dt-publish", RecordingAction.Returning(Action, new HostActionResult("published", Vars())));
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await arranged.Dispatcher.DispatchAsync(arranged.Message with { Seq = arranged.Message.Seq + 5 }, CancellationToken.None);

        DispatchOf(capture, arranged.Message.RunId).GetTagItem(WorkflowTelemetry.OutcomeTag).Should().Be(WorkflowTelemetry.OutcomeSkipped);
    }

    /// <summary>
    /// An exception the outbox retries is not a node outcome.
    /// Red: tag <c>thalos.outcome</c> = "failed" in the exception path; the no-outcome assertion fails.
    /// Red: drop the <c>error.type</c> SetTag; its assertion fails.
    /// Red: drop the SetStatus in the exception path; the status assertion fails.
    /// Red: record nothing on the exception path; the histogram assertion finds no measurement.
    /// </summary>
    [Fact]
    public async Task An_escaping_exception_ends_the_span_and_the_histogram_with_its_type_and_no_outcome()
    {
        var arranged = await ArrangeAsync(ActionYaml, "dt-process", "dt-publish", RecordingAction.Throwing(Action, new InvalidOperationException("boom")));
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        var dispatch = async () => await arranged.Dispatcher.DispatchAsync(arranged.Message, CancellationToken.None);
        await dispatch.Should().ThrowAsync<InvalidOperationException>();

        var span = DispatchOf(capture, arranged.Message.RunId);
        span.GetTagItem(WorkflowTelemetry.OutcomeTag).Should().BeNull();
        span.GetTagItem(WorkflowTelemetry.ErrorTypeTag).Should().Be(typeof(InvalidOperationException).FullName);
        span.Status.Should().Be(ActivityStatusCode.Error);
        capture.MeasurementsOf(WorkflowTelemetry.DispatchDurationName)
            .Should().Contain(m => Equals(m.Tags.GetValueOrDefault(WorkflowTelemetry.ErrorTypeTag), typeof(InvalidOperationException).FullName)
                && Equals(m.Tags.GetValueOrDefault(WorkflowTelemetry.NodeTag), "dt-publish"));
    }

    /// <summary>
    /// The action takes 50 ms, so the dispatch takes at least 40.
    /// Red: record <c>0</c> instead of the elapsed time; the value assertion fails.
    /// Red: tag the run id on the histogram; the exact-tags assertion fails.
    /// </summary>
    [Fact]
    public async Task The_dispatch_duration_is_recorded_by_node_and_outcome()
    {
        var slow = new RecordingAction(Action, async ct =>
        {
            await Task.Delay(50, ct);
            return Result<HostActionResult>.Success(new HostActionResult("published", Vars()));
        });
        var arranged = await ArrangeAsync(ActionYaml, "dt-process", "dt-publish", slow);
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await arranged.Dispatcher.DispatchAsync(arranged.Message, CancellationToken.None);

        var measurement = capture.MeasurementsOf(WorkflowTelemetry.DispatchDurationName)
            .Single(m => Equals(m.Tags.GetValueOrDefault(WorkflowTelemetry.NodeTag), "dt-publish"));
        measurement.Value.Should().BeGreaterThanOrEqualTo(40);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [WorkflowTelemetry.NodeTag] = "dt-publish",
            [WorkflowTelemetry.OutcomeTag] = WorkflowTelemetry.OutcomeCompleted,
        });
    }

    /// <summary>
    /// Red: pass <c>links: null</c> to <c>StartActivity</c>; the span has no link.
    /// Red: parent the span on the stored context instead of linking it; the link count is 0 and the trace id is the origin's.
    /// </summary>
    [Fact]
    public async Task A_run_started_under_an_activity_links_its_dispatch_to_that_context()
    {
        var arranged = await ArrangeAsync(ActionYaml, "dt-process", "dt-publish", RecordingAction.Returning(Action, new HostActionResult("published", Vars())), underOrigin: true);
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await arranged.Dispatcher.DispatchAsync(arranged.Message, CancellationToken.None);

        var span = DispatchOf(capture, arranged.Message.RunId);
        var link = span.Links.Should().ContainSingle().Subject;
        link.Context.TraceId.Should().Be(arranged.Origin!.Value.TraceId);
        link.Context.SpanId.Should().Be(arranged.Origin.Value.SpanId);
        span.TraceId.Should().NotBe(arranged.Origin.Value.TraceId, "each step is its own trace, linked rather than parented");
    }

    /// <summary>
    /// Red: call <c>ActivityContext.Parse</c> instead of <c>TryParse</c>; the malformed row throws.
    /// Red: add a default <c>ActivityLink</c> when there is no traceparent; the null row has a link.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-traceparent")]
    public async Task A_run_without_a_usable_traceparent_dispatches_without_a_link(string? stored)
    {
        var arranged = await ArrangeAsync(ActionYaml, "dt-process", "dt-publish", RecordingAction.Returning(Action, new HostActionResult("published", Vars())));
        arranged.Store.SetTraceParent(arranged.Message.RunId, stored);
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await arranged.Dispatcher.DispatchAsync(arranged.Message, CancellationToken.None);

        DispatchOf(capture, arranged.Message.RunId).Links.Should().BeEmpty();
    }

    private static Activity DispatchOf(TelemetryCapture capture, Guid runId) =>
        capture.SpansNamed(WorkflowTelemetry.DispatchSpanName).Should()
            .ContainSingle(s => runId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag))).Subject;

    private static async Task<Arranged> ArrangeAsync(
        string yaml, string process, string startNode, IWorkflowHostAction? action = null, bool underOrigin = false)
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(yaml);
        var store = new FakeWorkflowStore(definitions);
        var runner = new FakeSubagentRunner
        {
            NextResult = _ => Result<AgentTurnResult, AgentError>.Success(
                new AgentTurnResult(TurnId.New(), SessionId.New(), "done", TurnUsage.Empty("test-model"), [], TimeSpan.Zero)),
        };
        var dispatcher = new WorkflowNodeDispatcher(
            store, runner, new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal)),
            definitions, new InMemorySkillStore(TimeProvider.System), _ => new FakeSecurityContext("workflow-engine"),
            [], action is null ? [] : [action]);

        var request = new WorkflowStartRequest
        {
            Process = process,
            Version = 1,
            CorrelationKey = $"c-{Guid.NewGuid():N}",
            StartNode = startNode,
            InitialVariables = null,
            StartedBy = TestPrincipals.Starter,
        };

        ActivityContext? origin = null;
        Guid runId;
        if (underOrigin)
        {
            using var activity = new Activity("probe-origin").SetIdFormat(ActivityIdFormat.W3C).Start();
            origin = activity.Context;
            runId = (await store.StartAsync(request, CancellationToken.None)).Value;
        }
        else
        {
            runId = (await store.StartAsync(request, CancellationToken.None)).Value;
        }

        return new Arranged(dispatcher, store, store.TakeNext(runId)!, origin);
    }

    private static Dictionary<string, object?> Vars() => new(StringComparer.Ordinal);

    private sealed record Arranged(WorkflowNodeDispatcher Dispatcher, FakeWorkflowStore Store, WorkflowDispatchMessage Message, ActivityContext? Origin);
}
