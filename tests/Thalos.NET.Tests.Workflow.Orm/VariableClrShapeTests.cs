using System.Text.Json;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Review Focus 4 of the reflection-out change: values are converted to JSON only to be stored, so after a start, a
/// node result and a resume, <see cref="WorkflowRun.Variables"/> still holds the plain CLR values a consumer's
/// <c>as string</c> or <c>is long</c> reads expect, and never a boxed <see cref="JsonElement"/>. A characterization
/// test: it passes on the reflective store too, and its Red names the change that breaks it.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class VariableClrShapeTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Yaml = """
        process: variable-clr-shape
        version: 1
        nodes:
          start: { next: gate }
          gate: { await: go, next: done }
          done: { terminal: succeeded }
        """;

    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();

        var options = new WorkflowOrmOptions { ConnectionString = pg.ConnectionString };
        var definitions = new OrmProcessDefinitionStore(options);
        var definition = ProcessLoader.Load(Yaml);
        definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : "");
        await definitions.UpsertAndActivateAsync(definition.Value, Yaml, CancellationToken.None);

        _store = new OrmWorkflowStore(options, definitions);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object?> Bag() => new(StringComparer.Ordinal)
    {
        ["s"] = "a",
        ["n"] = 3L,
        ["d"] = 1.5,
        ["b"] = true,
        ["z"] = null,
        ["l"] = new List<object?> { "x" },
        ["m"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = "v" },
    };

    // Red: in OrmWorkflowJson.DeserializeVariables, replace every value with WorkflowVariableJson.ToElement of it;
    // the bag then holds boxed JsonElements and the type assertions fail after the start already.
    // Red: in WorkflowVariableBlock.ToPlainValue, read every number with GetDouble; "n" then reads as a double.
    [Fact]
    public async Task Run_variables_keep_their_CLR_types()
    {
        var runId = (await _store.StartAsync(
            new WorkflowStartRequest
            {
                Process = "variable-clr-shape",
                Version = 1,
                CorrelationKey = $"c-clr-shape-{Guid.NewGuid()}",
                StartNode = "start",
                InitialVariables = Bag(),
                StartedBy = TestPrincipals.Starter,
            },
            CancellationToken.None)).Value;

        AssertClrShapes((await _store.FindAsync(runId, CancellationToken.None))!, "after the start");

        await _store.CompleteNodeAsync(
            runId, seq: 1,
            new WorkflowTransition("gate", WorkflowStatus.Awaiting, "go", WorkflowEventKind.Awaiting),
            new NodeResult(null, Bag()), CancellationToken.None);

        AssertClrShapes((await _store.FindAsync(runId, CancellationToken.None))!, "after the node result");

        var resumed = await _store.ResumeAsync(
            runId, new WorkflowResumeRequest { Signal = "go", Payload = "p", ResumedBy = TestPrincipals.Approver }, CancellationToken.None);
        resumed.IsSuccess.Should().BeTrue(resumed.IsFailure ? resumed.Error : "");

        var run = (await _store.FindAsync(runId, CancellationToken.None))!;
        AssertClrShapes(run, "after the resume");
        run.Variables["payload"].Should().BeOfType<string>().Which.Should().Be("p");
    }

    private static void AssertClrShapes(WorkflowRun run, string because)
    {
        var v = run.Variables;
        v["s"].Should().BeOfType<string>(because).Which.Should().Be("a");
        v["n"].Should().BeOfType<long>(because).Which.Should().Be(3L);
        v["d"].Should().BeOfType<double>(because).Which.Should().Be(1.5);
        v["b"].Should().BeOfType<bool>(because).Which.Should().BeTrue();
        v.Should().ContainKey("z", because);
        v["z"].Should().BeNull(because);
        v["l"].Should().BeOfType<List<object?>>(because).Which.Should().Equal("x");
        v["m"].Should().BeOfType<Dictionary<string, object?>>(because).Which["k"].Should().BeOfType<string>(because).Which.Should().Be("v");
        v.Values.Should().NotContain(x => x is JsonElement, because);
    }
}
