using System.Diagnostics;
using Thalos;
using Thalos.Skills;
using Thalos.Testing;
using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>
/// 0.15.0, spec Part A step 4: the store's state-changing calls are spans, so a gate approval and an admin action are
/// visible. Reads are not, so the outbox poller's reads add no spans. A failed <c>Result</c> marks its span as an error.
/// The process names here, <c>st-*</c>, are this class's own.
/// </summary>
public sealed class StoreTelemetryTests
{
    private const string GateYaml = """
        process: st-gated
        version: 1
        nodes:
          st-gate: { await: human_approval, next: st-done }
          st-done: { terminal: succeeded }
        """;

    /// <summary>
    /// Red: drop the <c>[TraceTagFromResult]</c> on <c>StartAsync</c>; the run-id assertion fails.
    /// Red: drop the <c>[TraceTag]</c> on its request; the process assertion fails.
    /// </summary>
    [Fact]
    public async Task A_start_is_a_span_tagged_with_its_process_and_the_new_run_id()
    {
        var (store, _) = Arrange();
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        var runId = (await store.StartAsync(Request("st-start"), CancellationToken.None)).Value;

        var span = capture.SpansNamed(WorkflowTelemetry.StoreStartSpanName).Should().ContainSingle(s => runId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag))).Subject;
        span.GetTagItem(WorkflowTelemetry.ProcessTag).Should().Be("st-start");
        span.Status.Should().Be(ActivityStatusCode.Unset);
    }

    /// <summary>
    /// An over-cap variable bag fails the start with a Result.
    /// Red: drop <c>ErrorWhen = "IsFailure"</c> on <c>StartAsync</c>; the status is Unset.
    /// Red: drop the <c>When = "IsSuccess"</c> guard; reading <c>Value</c> on the failure throws.
    /// </summary>
    [Fact]
    public async Task A_refused_start_is_an_error_span_with_no_run_id_and_no_description()
    {
        var (store, _) = Arrange();
        var oversized = Enumerable.Range(0, 10_000).ToDictionary(i => $"k{i}", i => (object?)i, StringComparer.Ordinal);
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        var result = await store.StartAsync(Request("st-refused") with { InitialVariables = oversized }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        var span = capture.SpansNamed(WorkflowTelemetry.StoreStartSpanName).Single(s => "st-refused".Equals(s.GetTagItem(WorkflowTelemetry.ProcessTag)));
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().BeNull();
        span.GetTagItem(WorkflowTelemetry.RunIdTag).Should().BeNull();
    }

    /// <summary>
    /// Red: drop the <c>[Trace]</c> on <c>ResumeAsync</c>; no resume span exists.
    /// Red: drop the signal <c>[TraceTag]</c>; the signal assertion fails.
    /// Red: set <c>ErrorWhen = "IsSuccess"</c>; the approved resume is an Error span.
    /// </summary>
    [Fact]
    public async Task An_approved_resume_is_a_span_tagged_with_its_run_and_signal()
    {
        var (store, runId) = await ParkedAtGateAsync();
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        (await store.ResumeAsync(runId, new WorkflowResumeRequest { Signal = "human_approval", ResumedBy = TestPrincipals.Starter }, CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        var span = capture.SpansNamed(WorkflowTelemetry.StoreResumeSpanName).Should().ContainSingle(s => runId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag))).Subject;
        span.GetTagItem(WorkflowTelemetry.SignalTag).Should().Be("human_approval");
        span.Status.Should().Be(ActivityStatusCode.Unset);
    }

    /// <summary>Red: drop <c>ErrorWhen</c> on <c>ResumeAsync</c>; the refused resume is Unset.</summary>
    [Fact]
    public async Task A_refused_resume_is_an_error_span()
    {
        var (store, runId) = await ParkedAtGateAsync();
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await store.ResumeAsync(runId, new WorkflowResumeRequest { Signal = "st-wrong-signal", ResumedBy = TestPrincipals.Starter }, CancellationToken.None);

        capture.SpansNamed(WorkflowTelemetry.StoreResumeSpanName).Single(s => runId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag)))
            .Status.Should().Be(ActivityStatusCode.Error);
    }

    /// <summary>
    /// The calls a run makes are start, find and cancel; only start and cancel are traced, and the reason is in no tag.
    /// Red: add <c>[Trace("thalos.workflow.store.find")]</c> and <c>[TraceTag(WorkflowTelemetry.RunIdTag)]</c> on <c>runId</c> to
    /// <c>FindAsync</c>; the name set gains it. The run-id filter hides a span without the tag, so the tag is part of the change.
    /// Red: drop the <c>[Trace]</c> on <c>CancelAsync</c>; the name set lacks it.
    /// Red: add <c>[TraceTag("thalos.reason")]</c> to the cancel reason; the content assertion fails.
    /// </summary>
    [Fact]
    public async Task Only_state_changes_are_traced_and_a_cancel_reason_is_in_no_tag()
    {
        var (store, _) = Arrange();
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        var runId = (await store.StartAsync(Request("st-cancel"), CancellationToken.None)).Value;
        await store.FindAsync(runId, CancellationToken.None);
        await store.FindStrandedAsync(TimeSpan.FromDays(1), CancellationToken.None);
        await store.CancelAsync(runId, "zqx-secret-reason", CancellationToken.None);

        var spans = capture.Spans.Where(s => runId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag))).ToList();
        spans.Select(s => s.OperationName).Should().BeEquivalentTo([WorkflowTelemetry.StoreStartSpanName, WorkflowTelemetry.StoreCancelSpanName]);
        spans.SelectMany(s => s.TagObjects).Select(t => t.Value?.ToString()).Should().NotContain(v => v != null && v.Contains("zqx", StringComparison.Ordinal));
    }

    /// <summary>
    /// Red: drop the <c>[Trace]</c> on <c>FailAsync</c>; no fail span exists.
    /// Red: add <c>[TraceTag("thalos.error")]</c> to the error message; the content assertion fails.
    /// </summary>
    [Fact]
    public async Task A_fail_is_a_span_tagged_with_its_run_and_not_its_message()
    {
        var (store, _) = Arrange();
        var runId = (await store.StartAsync(Request("st-fail"), CancellationToken.None)).Value;
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        await store.FailAsync(runId, "zqx-secret-error", CancellationToken.None);

        var span = capture.SpansNamed(WorkflowTelemetry.StoreFailSpanName).Should().ContainSingle(s => runId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag))).Subject;
        span.TagObjects.Select(t => t.Value?.ToString()).Should().NotContain(v => v != null && v.Contains("zqx", StringComparison.Ordinal));
    }

    /// <summary>
    /// Red: drop the <c>[TraceTag]</c> on <c>expectedSeq</c>; the seq assertion fails.
    /// Red: drop the <c>[TraceTagFromResult]</c>; the failed-flag assertion fails.
    /// Red: drop <c>ErrorWhen</c> on <c>RetryFailedNodeAsync</c>; the refused retry is Unset.
    /// </summary>
    [Fact]
    public async Task A_stranded_fail_and_a_refused_retry_are_traced_with_their_seq()
    {
        var (store, _) = Arrange();
        var runId = (await store.StartAsync(Request("st-stranded"), CancellationToken.None)).Value;
        using var capture = new TelemetryCapture(WorkflowTelemetry.SourceName);

        (await store.FailStrandedAsync(runId, 1, "stranded", CancellationToken.None)).Should().BeTrue();
        await store.RetryFailedNodeAsync(runId, new WorkflowRetryRequest { ExpectedSeq = 99, RetriedBy = TestPrincipals.Starter }, CancellationToken.None);

        var stranded = capture.SpansNamed(WorkflowTelemetry.StoreFailStrandedSpanName).Single(s => runId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag)));
        stranded.GetTagItem(WorkflowTelemetry.SeqTag).Should().Be(1L);
        stranded.GetTagItem(WorkflowTelemetry.StrandedFailedTag).Should().Be(true);
        var retry = capture.SpansNamed(WorkflowTelemetry.StoreRetrySpanName).Single(s => runId.Equals(s.GetTagItem(WorkflowTelemetry.RunIdTag)));
        retry.GetTagItem(WorkflowTelemetry.SeqTag).Should().Be(99L);
        retry.Status.Should().Be(ActivityStatusCode.Error);
    }

    private static (WorkflowStoreInstrumented Store, InMemoryProcessDefinitionStore Definitions) Arrange()
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(GateYaml);
        return (new WorkflowStoreInstrumented(new FakeWorkflowStore(definitions)), definitions);
    }

    /// <summary>A run of <c>st-gated</c>, dispatched once over the unproxied fake so it parks at its gate.</summary>
    private static async Task<(WorkflowStoreInstrumented Store, Guid RunId)> ParkedAtGateAsync()
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(GateYaml);
        var fake = new FakeWorkflowStore(definitions);
        var dispatcher = new WorkflowNodeDispatcher(
            fake, new FakeSubagentRunner(), new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal)),
            definitions, new InMemorySkillStore(TimeProvider.System), _ => new FakeSecurityContext("workflow-engine"), [], []);
        var runId = (await fake.StartAsync(Request("st-gated") with { StartNode = "st-gate" }, CancellationToken.None)).Value;
        await dispatcher.DispatchAsync(fake.TakeNext(runId)!, CancellationToken.None);
        (await fake.FindAsync(runId, CancellationToken.None))!.Status.Should().Be(WorkflowStatus.Awaiting);
        return (new WorkflowStoreInstrumented(fake), runId);
    }

    private static WorkflowStartRequest Request(string process) => new()
    {
        Process = process,
        Version = 1,
        CorrelationKey = $"c-{Guid.NewGuid():N}",
        StartNode = "st-gate",
        InitialVariables = null,
        StartedBy = TestPrincipals.Starter,
    };
}
