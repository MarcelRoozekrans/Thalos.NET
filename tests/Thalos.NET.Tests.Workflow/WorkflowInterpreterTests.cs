using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Tests for <see cref="WorkflowInterpreter.Advance"/> over its evaluation order — gate, then resolve the edge
/// via <c>branch</c> or <c>next</c>, then cap-check the resolved target, then terminal — against the same
/// "manufacture" fixture <see cref="ProcessLoaderTests"/> uses:
/// <c>implement -[next]-> review -[branch: approved]-> gate -[next]-> publish -[next]-> done</c>, with
/// <c>review</c>'s <c>rejected</c> outcome looping back to <c>implement</c>, capped at <c>maxVisits: 5</c> with
/// <c>onExceeded: adjudicate</c>. Because the cap is checked on the edge's resolved target rather than the node
/// reporting the result, <c>review</c>'s own cap can only ever fire on the edge that re-enters <c>review</c> —
/// <c>implement</c>'s <c>next</c> — never on the edge that leaves <c>review</c> for <c>gate</c>. <c>gate</c>
/// itself behaves two ways depending on <see cref="WorkflowRun.Status"/>: arriving (any status but
/// <see cref="WorkflowStatus.Awaiting"/>) parks the run there, while resuming (status already
/// <see cref="WorkflowStatus.Awaiting"/>) follows its <c>next</c> edge to <c>publish</c> instead.
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

    /// <summary>
    /// The other half of the gate primitive, and the one that did not exist before this test was added: without
    /// checking <see cref="WorkflowRun.Status"/>, <c>Advance</c> would park a resumed run right back on the
    /// gate it was just resumed from, making <c>gate</c>'s <c>next</c> edge to <c>publish</c> permanently
    /// unreachable. A run whose status is already <see cref="WorkflowStatus.Awaiting"/> when <c>Advance</c> is
    /// called on its gate is being resumed, not arriving, so this resolves <c>next</c> like any other node —
    /// with <see cref="WorkflowEventKind.Resumed"/> in place of <see cref="WorkflowEventKind.Completed"/>.
    /// </summary>
    [Fact]
    public void Advance_resumes_a_gate_and_follows_its_next_edge()
    {
        var run = RunAt("gate") with { Status = WorkflowStatus.Awaiting, AwaitingSignal = "human_approval" };

        var t = WorkflowInterpreter.Advance(Def, run, new NodeResult(null, Empty)).Value;

        t.NextNode.Should().Be("publish");
        t.NextStatus.Should().Be(WorkflowStatus.Running);
        t.Kind.Should().Be(WorkflowEventKind.Resumed);
    }

    /// <summary>
    /// The cap belongs to the node being entered, not the node reporting a result — so <c>review</c>'s own
    /// completion can never trip its own cap; only an edge that re-enters <c>review</c> can. That edge is
    /// <c>implement</c>'s <c>next</c>, so this drives the cap from <c>implement</c>'s perspective:
    /// <c>Visits["review"]</c> already at 5 means the entry <c>implement</c>'s <c>next</c> is about to cause
    /// would be <c>review</c>'s sixth, which <c>maxVisits: 5</c> forbids — redirected to <c>review</c>'s own
    /// <c>onExceeded</c>, <c>adjudicate</c>, instead.
    /// </summary>
    [Fact]
    public void Advance_routes_to_onExceeded_when_maxVisits_is_reached()
    {
        var run = RunAt("implement") with { Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["review"] = 5 } };

        var t = WorkflowInterpreter.Advance(Def, run, new NodeResult(null, Empty)).Value;

        t.NextNode.Should().Be("adjudicate");
        t.Kind.Should().Be(WorkflowEventKind.CapExceeded);
    }

    /// <summary>
    /// The boundary case the cap-exceeded test alone cannot distinguish from an off-by-one: with <c>review</c>
    /// already entered 4 times, <c>implement</c>'s <c>next</c> would cause its 5th entry, and
    /// <c>maxVisits: 5</c> must still allow that one through rather than redirecting to <c>onExceeded</c>.
    /// Paired with <see cref="Advance_routes_to_onExceeded_when_maxVisits_is_reached"/>, which sets
    /// <c>Visits["review"] = 5</c> (the prospective 6th entry) and expects the opposite outcome — together they
    /// pin down that the comparison is <c>Visits[target] + 1 &gt; MaxVisits</c>, not <c>&gt;=</c> or unshifted.
    /// </summary>
    [Fact]
    public void Advance_allows_the_entry_that_reaches_the_cap_exactly()
    {
        var run = RunAt("implement") with { Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["review"] = 4 } };

        var t = WorkflowInterpreter.Advance(Def, run, new NodeResult(null, Empty)).Value;

        t.NextNode.Should().Be("review");
        t.Kind.Should().Be(WorkflowEventKind.Completed);
    }

    /// <summary>
    /// The case that motivated checking the cap against the resolved target instead of the node reporting the
    /// result: <c>review</c> is at its cap (5 prior entries) and reports <c>approved</c>, an outcome whose
    /// branch leaves <c>review</c> for the uncapped <c>gate</c>. The cap must not intercept this — it bounds
    /// re-entry into <c>review</c>, not exit from it. A cap check against the current/source node (the
    /// retracted design) would redirect this to <c>review</c>'s own <c>onExceeded</c>, discarding a passing
    /// result purely because it happened to arrive on the node's last allowed run.
    /// </summary>
    [Fact]
    public void Advance_does_not_discard_an_approved_outcome_at_the_visit_that_reaches_the_cap()
    {
        var run = RunAt("review") with { Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["review"] = 5 } };

        var t = WorkflowInterpreter.Advance(Def, run, new NodeResult("approved", Empty)).Value;

        t.NextNode.Should().Be("gate");
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

    private static readonly ProcessDefinition CapOnTargetDef = ProcessLoader.Load("""
        process: cap-on-target-not-source
        version: 1
        nodes:
          a: { agent: x, skill: y, next: b, maxVisits: 1, onExceeded: c }
          b: { terminal: succeeded }
          c: { terminal: failed }
        """).Value;

    /// <summary>
    /// Same rationale as <see cref="Def_satisfies_ProcessValidator_so_Advance_may_rely_on_its_guarantees"/>, for
    /// the dedicated fixture below.
    /// </summary>
    [Fact]
    public async Task CapOnTargetDef_satisfies_ProcessValidator_so_Advance_may_rely_on_its_guarantees()
    {
        var result = await ProcessValidator.ValidateAsync(CapOnTargetDef, resolver: null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
    }

    /// <summary>
    /// Replaces a previous "cap checked before next" fixture that pinned nothing once the cap moved from the
    /// current node to the edge's resolved target: that fixture's capped node looped back to itself, so "check
    /// my own cap" and "check the target's cap" landed on the identical node and could never disagree. Here
    /// they differ — <c>a</c> carries the cap, but its <c>next</c> edge leaves for the uncapped <c>b</c> — so an
    /// implementation that still checked the cap on the current/source node (the retracted design) would
    /// redirect to <c>c</c> via <c>a</c>'s <c>onExceeded</c>; the correct one checks <c>b</c>'s cap (none) and
    /// proceeds to <c>b</c>.
    /// </summary>
    [Fact]
    public void Advance_checks_the_cap_on_the_targets_own_maxVisits_not_the_source_nodes()
    {
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            Process = "cap-on-target-not-source",
            ProcessVersion = 1,
            CurrentNode = "a",
            CurrentSeq = 1,
            Status = WorkflowStatus.Running,
            AwaitingSignal = null,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 1 },
            LastError = null,
        };

        var t = WorkflowInterpreter.Advance(CapOnTargetDef, run, new NodeResult(null, Empty)).Value;

        t.NextNode.Should().Be("b");
        t.Kind.Should().Be(WorkflowEventKind.Completed);
    }
}
