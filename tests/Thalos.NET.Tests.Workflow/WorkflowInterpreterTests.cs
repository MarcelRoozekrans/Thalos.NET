using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Tests for <see cref="WorkflowInterpreter.Advance"/> over the five evaluation steps — cap, gate, branch,
/// <c>next</c>, terminal — against the same "manufacture" fixture <see cref="ProcessLoaderTests"/> uses:
/// <c>implement -[next]-> review -[branch: approved]-> gate -[next]-> publish -[next]-> done</c>, with
/// <c>review</c>'s <c>rejected</c> outcome looping back to <c>implement</c>, capped at <c>maxVisits: 5</c> with
/// <c>onExceeded: adjudicate</c>.
/// </summary>
public sealed class WorkflowInterpreterTests
{
    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>(StringComparer.Ordinal);

    private static readonly ProcessDefinition Def = ProcessLoader.Load("""
        process: manufacture
        version: 3
        nodes:
          implement: { agent: backend, skill: tdd, next: review }
          review:
            agent: reviewer
            skill: code-review
            outcomes: [approved, rejected]
            branch: { approved: gate, rejected: implement }
            maxVisits: 5
            onExceeded: adjudicate
          gate: { await: human_approval, next: publish }
          publish: { agent: publisher, skill: finish, next: done }
          adjudicate: { agent: lead, skill: adjudicate, next: done }
          done: { terminal: succeeded }
        """).Value;

    private static WorkflowRun RunAt(string node) => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 3,
        CurrentNode = node,
        CurrentSeq = 1,
        Status = WorkflowStatus.Running,
        AwaitingSignal = null,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
        LastError = null,
    };

    /// <summary>
    /// <see cref="WorkflowInterpreter.Advance"/> is entitled to rely on <see cref="ProcessValidator"/>'s
    /// guarantees and does not re-check them, so <see cref="Def"/> has to actually satisfy them — otherwise the
    /// fixture could drift into violating one and no test above would notice.
    /// </summary>
    [Fact]
    public async Task Def_satisfies_ProcessValidator_so_Advance_may_rely_on_its_guarantees()
    {
        var result = await ProcessValidator.ValidateAsync(Def, resolver: null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
    }

    [Fact]
    public void Advance_follows_next_for_a_sequence_node()
    {
        var t = WorkflowInterpreter.Advance(Def, RunAt("implement"), new NodeResult(null, Empty)).Value;

        t.NextNode.Should().Be("review");
        t.NextStatus.Should().Be(WorkflowStatus.Running);
        t.Kind.Should().Be(WorkflowEventKind.Completed);
    }

    [Fact]
    public void Advance_selects_the_branch_matching_the_declared_outcome()
    {
        var t = WorkflowInterpreter.Advance(Def, RunAt("review"), new NodeResult("approved", Empty)).Value;

        t.NextNode.Should().Be("gate");
        t.NextStatus.Should().Be(WorkflowStatus.Running);
        t.Kind.Should().Be(WorkflowEventKind.Branched);
    }

    [Fact]
    public void Advance_follows_a_loop_back_edge()
    {
        var t = WorkflowInterpreter.Advance(Def, RunAt("review"), new NodeResult("rejected", Empty)).Value;

        t.NextNode.Should().Be("implement");
        t.Kind.Should().Be(WorkflowEventKind.Branched);
    }

    [Fact]
    public void Advance_parks_at_a_gate_with_the_awaiting_signal_set()
    {
        var t = WorkflowInterpreter.Advance(Def, RunAt("gate"), new NodeResult(null, Empty)).Value;

        t.NextNode.Should().Be("gate");
        t.NextStatus.Should().Be(WorkflowStatus.Awaiting);
        t.AwaitingSignal.Should().Be("human_approval");
        t.Kind.Should().Be(WorkflowEventKind.Awaiting);
    }

    [Fact]
    public void Advance_routes_to_onExceeded_when_maxVisits_is_reached()
    {
        var run = RunAt("review") with { Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["review"] = 5 } };

        var t = WorkflowInterpreter.Advance(Def, run, new NodeResult("rejected", Empty)).Value;

        t.NextNode.Should().Be("adjudicate");
        t.Kind.Should().Be(WorkflowEventKind.CapExceeded);
    }

    /// <summary>
    /// The boundary case the cap-exceeded test alone cannot distinguish from an off-by-one: with 4 prior
    /// completions, this is the 5th, and <c>maxVisits: 5</c> must still allow it to take its normal branch
    /// rather than <c>onExceeded</c>. Paired with <see cref="Advance_routes_to_onExceeded_when_maxVisits_is_reached"/>,
    /// which sets <c>Visits["review"] = 5</c> (the 6th completion) and expects the opposite outcome — together
    /// they pin down that the comparison is <c>Visits[node] + 1 &gt; MaxVisits</c>, not <c>&gt;=</c> or unshifted.
    /// </summary>
    [Fact]
    public void Advance_allows_the_completion_that_reaches_the_cap_exactly()
    {
        var run = RunAt("review") with { Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["review"] = 4 } };

        var t = WorkflowInterpreter.Advance(Def, run, new NodeResult("rejected", Empty)).Value;

        t.NextNode.Should().Be("implement");
        t.Kind.Should().Be(WorkflowEventKind.Branched);
    }

    [Fact]
    public void Advance_completes_a_terminal_node_with_its_declared_status()
    {
        var t = WorkflowInterpreter.Advance(Def, RunAt("done"), new NodeResult(null, Empty)).Value;

        t.NextNode.Should().Be("done");
        t.NextStatus.Should().Be(WorkflowStatus.Succeeded);
        t.Kind.Should().Be(WorkflowEventKind.Completed);
    }

    [Fact]
    public void Advance_fails_when_the_outcome_is_not_a_declared_one()
    {
        var t = WorkflowInterpreter.Advance(Def, RunAt("review"), new NodeResult("approved, with concerns", Empty));

        t.IsFailure.Should().BeTrue();
        t.Error.Should().Contain("approved, with concerns").And.Contain("review");
    }

    /// <summary>
    /// No node in <see cref="Def"/> combines <c>maxVisits</c> with a plain <c>next</c> edge — <c>review</c>,
    /// the only capped node, routes through <c>branch</c> instead — so nothing above would notice the cap
    /// check being moved below <c>next</c> in the evaluation order. A dedicated fixture pins it: a capped node
    /// whose only outgoing edge is <c>next</c>, revisited past its cap, must still be redirected to
    /// <c>onExceeded</c> rather than looping forever on <c>next</c>.
    /// </summary>
    [Fact]
    public void Advance_checks_the_cap_before_taking_next_on_a_plain_sequence_node()
    {
        var def = ProcessLoader.Load("""
            process: cap-before-next
            version: 1
            nodes:
              loop: { agent: x, skill: y, next: loop, maxVisits: 3, onExceeded: done }
              done: { terminal: succeeded }
            """).Value;
        var run = RunAt("loop") with
        {
            Process = "cap-before-next",
            ProcessVersion = 1,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["loop"] = 3 },
        };

        var t = WorkflowInterpreter.Advance(def, run, new NodeResult(null, Empty)).Value;

        t.NextNode.Should().Be("done");
        t.Kind.Should().Be(WorkflowEventKind.CapExceeded);
    }
}
