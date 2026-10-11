using System.Diagnostics;
using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>The fake must mirror <c>OrmWorkflowStore</c>'s traceparent capture, or tests built on it drift.</summary>
public sealed class FakeWorkflowStoreTraceParentTests
{
    /// <summary>Red: drop the W3C check from <c>CurrentTraceParent</c>; the hierarchical id is stored.</summary>
    [Fact]
    public async Task A_hierarchical_activity_leaves_no_traceparent()
    {
        var store = new FakeWorkflowStore(new InMemoryProcessDefinitionStore());
        Guid runId;
        using (new Activity("probe-fake-hierarchical").SetIdFormat(ActivityIdFormat.Hierarchical).Start())
        {
            runId = (await store.StartAsync(Request(), CancellationToken.None)).Value;
        }

        (await store.FindAsync(runId, CancellationToken.None))!.TraceParent.Should().BeNull();
    }

    /// <summary>Red: return null always from <c>CurrentTraceParent</c>; the stored value is null.</summary>
    [Fact]
    public async Task A_w3c_activity_is_stored_as_the_traceparent()
    {
        var store = new FakeWorkflowStore(new InMemoryProcessDefinitionStore());
        string expected;
        Guid runId;
        using (var origin = new Activity("probe-fake-w3c").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            expected = origin.Id!;
            runId = (await store.StartAsync(Request(), CancellationToken.None)).Value;
        }

        (await store.FindAsync(runId, CancellationToken.None))!.TraceParent.Should().Be(expected);
    }

    private static WorkflowStartRequest Request() => new()
    {
        Process = "trace-parent",
        Version = 1,
        CorrelationKey = Guid.NewGuid().ToString(),
        StartNode = "implement",
        InitialVariables = null,
        StartedBy = new RunPrincipal("test-starter", ["admin"]),
    };
}
