using Thalos;
using Thalos.Skills;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// <see cref="WorkflowNodeDispatcher"/> honouring a run's <see cref="RunManifest"/>: a pinned task node runs the
/// exact agent revision and skill body the run was started against, even after either is edited or republished
/// later; an unpinned run (<see cref="WorkflowRun.Manifest"/> is <see langword="null"/>) keeps today's
/// name-resolution behaviour; and a manifest that does not name the run's current node fails the node rather than
/// falling back to resolving it live — a manifest is all or nothing.
/// </summary>
/// <remarks>
/// Uses <see cref="FakeWorkflowStore"/>, <see cref="FakeSubagentRunner"/> and <see cref="FakeWorkflowReferenceResolver"/>
/// for the same reasons <see cref="ConstrainedOutcomeTests"/> does, plus an <see cref="InMemorySkillStore"/> in
/// place of a real skill sync — no Postgres, no Docker, no file system.
/// </remarks>
public sealed class RunPinningDispatchTests
{
    private static readonly AgentId ImplementerId = AgentId.New();
    private static readonly TimeProvider Clock = TimeProvider.System;

    /// <summary>One task node feeding a terminal — the minimum shape a pinned dispatch needs to exercise.</summary>
    private const string PinnedProcessYaml = """
        process: pinned
        version: 1
        nodes:
          implement:
            agent: implementer
            skill: manufacture-implement
            next: done
          done: { terminal: succeeded }
        """;

    [Fact]
    public async Task A_pinned_node_runs_the_pinned_skill_text_even_after_the_skill_changed()
    {
        var skills = new InMemorySkillStore(Clock);
        await skills.UpsertAsync(Skill("manufacture-implement", "h1", body: "Pinned body."), CancellationToken.None);
        await skills.UpsertAsync(Skill("manufacture-implement", "h2", body: "Edited body."), CancellationToken.None);
        var (dispatcher, runner, message, _) = await ArrangePinnedRunAsync(
            skills, pin: new NodePin("implementer", ImplementerId, "r1", "manufacture-implement", "h1"));

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        var request = runner.Requests.Should().ContainSingle().Which;
        request.Task.Should().Contain("Pinned body.").And.NotContain("Edited body.");
        request.Task.Should().NotContain("Use skill", "a pinned node is handed its text, not told to fetch the latest");
        request.AgentRevision.Should().Be("r1");
        request.AgentId.Should().Be(ImplementerId);
    }

    [Fact]
    public async Task A_missing_pinned_skill_fails_the_node_and_never_falls_back_to_latest()
    {
        var skills = new InMemorySkillStore(Clock);
        await skills.UpsertAsync(Skill("manufacture-implement", "h2", body: "Latest."), CancellationToken.None);
        var (dispatcher, runner, message, store) = await ArrangePinnedRunAsync(
            skills, pin: new NodePin("implementer", ImplementerId, null, "manufacture-implement", "h1"));

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        runner.Requests.Should().BeEmpty();
        var run = await store.FindAsync(message.RunId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("manufacture-implement@h1");
    }

    /// <summary>
    /// <see cref="NodePin.SkillName"/> is a plain <see langword="string"/>, not the validated <see cref="SkillName"/>
    /// type, because a manifest round-trips through <c>JsonSerializer</c> and a database column — nothing at
    /// compile time stops a corrupted row from holding something <see cref="SkillName.TryParse"/> would refuse. Red
    /// if the dispatcher parses it with <see cref="SkillName.Parse(string)"/> instead of <c>TryParse</c>: that
    /// throws <see cref="FormatException"/>, which escapes <see cref="WorkflowNodeDispatcher.DispatchAsync"/>
    /// uncaught — see this class's own remarks on why a node failure must never throw — and reaches the outbox as
    /// an infrastructure fault instead of landing on the run as <see cref="WorkflowStatus.Failed"/>.
    /// </summary>
    [Fact]
    public async Task A_pinned_skill_name_that_is_not_a_valid_skill_name_fails_the_node_rather_than_throwing()
    {
        var skills = new InMemorySkillStore(Clock);
        var (dispatcher, runner, message, store) = await ArrangePinnedRunAsync(
            skills, pin: new NodePin("implementer", ImplementerId, "r1", "Not A Valid Name!", "h1"));

        var dispatch = async () => await dispatcher.DispatchAsync(message, CancellationToken.None);

        await dispatch.Should().NotThrowAsync();
        runner.Requests.Should().BeEmpty();
        var run = await store.FindAsync(message.RunId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("Not A Valid Name!");
    }

    [Fact]
    public async Task An_unpinned_run_keeps_the_pre_manifest_behaviour()
    {
        var (dispatcher, runner, message) = await ArrangeUnpinnedRunAsync();

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        var request = runner.Requests.Should().ContainSingle().Which;
        request.Task.Should().StartWith("Use skill 'manufacture-implement'");
        request.AgentRevision.Should().BeNull();
    }

    [Fact]
    public async Task A_manifest_present_but_missing_the_current_node_fails_it_instead_of_resolving_live()
    {
        var skills = new InMemorySkillStore(Clock);
        await skills.UpsertAsync(Skill("manufacture-implement", "h1", body: "Pinned body."), CancellationToken.None);
        var definitions = new InMemoryProcessDefinitionStore().Seed(PinnedProcessYaml);
        var resolver = new FakeWorkflowReferenceResolver(
            new Dictionary<string, AgentId>(StringComparer.Ordinal) { ["implementer"] = ImplementerId });
        var store = new FakeWorkflowStore(definitions);
        var runner = new FakeSubagentRunner();
        var dispatcher = new WorkflowNodeDispatcher(store, runner, resolver, definitions, skills, _ => new FakeSecurityContext("workflow-engine"));

        // Manifest carries nodes, but not "implement" — the node this run actually starts at.
        var manifest = new RunManifest { Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal) };
        var runId = await store.StartAsync(
            new WorkflowStartRequest { Process = "pinned", Version = 1, CorrelationKey = "c-manifest-gap", StartNode = "implement", Manifest = manifest },
            CancellationToken.None);
        var message = store.TakeNext(runId)!;

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        runner.Requests.Should().BeEmpty("a manifest is all or nothing - a node it does not name must never be resolved live");
        var run = await store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("implement").And.Contain("manifest");
    }

    /// <summary>
    /// Builds a dispatcher over a single-node <c>pinned</c> process, starts a run with <paramref name="pin"/> as
    /// its only manifest entry, and returns the message the store enqueued for it, still undispatched.
    /// </summary>
    private static async Task<(WorkflowNodeDispatcher Dispatcher, FakeSubagentRunner Runner, WorkflowDispatchMessage Message, FakeWorkflowStore Store)> ArrangePinnedRunAsync(
        ISkillStore skills, NodePin pin)
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(PinnedProcessYaml);
        // Empty on purpose: a pinned node must never resolve its agent name live, so a resolver that could answer
        // "implementer" is exactly what would let a resolver call hide inside a passing test.
        var resolver = new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal));
        var store = new FakeWorkflowStore(definitions);
        var runner = new FakeSubagentRunner
        {
            NextResult = _ => Result<AgentTurnResult, AgentError>.Success(
                new AgentTurnResult(TurnId.New(), SessionId.New(), "done", TurnUsage.Empty("test-model"), [], TimeSpan.Zero)),
        };
        var dispatcher = new WorkflowNodeDispatcher(store, runner, resolver, definitions, skills, _ => new FakeSecurityContext("workflow-engine"));

        var manifest = new RunManifest { Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal) { ["implement"] = pin } };
        var runId = await store.StartAsync(
            new WorkflowStartRequest { Process = "pinned", Version = 1, CorrelationKey = "c-pinned", StartNode = "implement", Manifest = manifest },
            CancellationToken.None);

        return (dispatcher, runner, store.TakeNext(runId)!, store);
    }

    /// <summary>
    /// Builds a dispatcher over the same single-node process, started with no manifest at all — the shape every
    /// run had before pinning existed, and what a run started through the legacy positional
    /// <see cref="IWorkflowStore.StartAsync(string,int,string,string,IReadOnlyDictionary{string,object?}?,CancellationToken)"/>
    /// overload still gets.
    /// </summary>
    private static async Task<(WorkflowNodeDispatcher Dispatcher, FakeSubagentRunner Runner, WorkflowDispatchMessage Message)> ArrangeUnpinnedRunAsync()
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(PinnedProcessYaml);
        var resolver = new FakeWorkflowReferenceResolver(
            new Dictionary<string, AgentId>(StringComparer.Ordinal) { ["implementer"] = ImplementerId });
        var store = new FakeWorkflowStore(definitions);
        var runner = new FakeSubagentRunner
        {
            NextResult = _ => Result<AgentTurnResult, AgentError>.Success(
                new AgentTurnResult(TurnId.New(), SessionId.New(), "done", TurnUsage.Empty("test-model"), [], TimeSpan.Zero)),
        };
        var skills = new InMemorySkillStore(Clock); // never touched by the unpinned path
        var dispatcher = new WorkflowNodeDispatcher(store, runner, resolver, definitions, skills, _ => new FakeSecurityContext("workflow-engine"));

        var runId = await store.StartAsync("pinned", 1, "c-unpinned", "implement", initialVariables: null, CancellationToken.None);
        return (dispatcher, runner, store.TakeNext(runId)!);
    }

    private static SkillDocument Skill(string name, string hash, string body) => new()
    {
        Name = SkillName.Parse(name),
        Description = "a test skill",
        Body = body,
        SourcePath = $"{name}/SKILL.md",
        ContentHash = hash,
        UpdatedAt = Clock.GetUtcNow(),
    };
}
