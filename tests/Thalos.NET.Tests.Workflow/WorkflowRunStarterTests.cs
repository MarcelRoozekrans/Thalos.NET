using Thalos;
using Thalos.Skills;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Tests for <see cref="WorkflowRunStarter"/>: resolves a process's active version, pins every task node through
/// an <see cref="IRunManifestResolver"/>, and starts the run — with no partial write when pinning fails.
/// </summary>
public sealed class WorkflowRunStarterTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    private const string TwoNodeProcessYaml = """
        process: manufacture
        version: 1
        nodes:
          implement:
            agent: implementer
            skill: manufacture-implement
            next: review
          review:
            agent: reviewer
            skill: manufacture-review
            next: done
          done: { terminal: succeeded }
        """;

    [Fact]
    public async Task An_unresolvable_node_starts_nothing()
    {
        var (store, starter) = await StarterWith(
            agents: [Def("implementer"), Def("reviewer")],
            skills: [Skill("manufacture-implement", "h")]); // review's skill is missing

        var started = await starter.StartAsync(
            new WorkflowRunStartOptions { Process = "manufacture", CorrelationKey = "k1", StartedBy = TestPrincipals.Starter },
            CancellationToken.None);

        started.IsFailure.Should().BeTrue();
        started.Error.Should().Contain("review");
        store.StartedRuns.Should().BeEmpty("a partial pin is never written");
    }

    [Fact]
    public async Task A_fully_resolvable_process_starts_a_run_pinned_by_the_manifest()
    {
        var (store, starter) = await StarterWith(
            agents: [Def("implementer", "r-imp"), Def("reviewer", "r-rev")],
            skills: [Skill("manufacture-implement", "h-imp"), Skill("manufacture-review", "h-rev")]);

        var started = await starter.StartAsync(
            new WorkflowRunStartOptions { Process = "manufacture", CorrelationKey = "k1", StartedBy = TestPrincipals.Starter },
            CancellationToken.None);

        started.IsSuccess.Should().BeTrue(started.IsFailure ? started.Error : "");
        store.StartedRuns.Should().ContainSingle();
        var request = store.StartedRuns.Single();
        request.StartNode.Should().Be("implement", "WorkflowRunStarter must pass the definition's own StartNode, not guess at one");
        request.Manifest.Should().NotBeNull();
        request.Manifest!.Nodes.Keys.Should().BeEquivalentTo(["implement", "review"]);
    }

    [Fact]
    public async Task Documents_are_carried_into_the_manifest_unchanged()
    {
        var (store, starter) = await StarterWith(
            agents: [Def("implementer"), Def("reviewer")],
            skills: [Skill("manufacture-implement", "h-imp"), Skill("manufacture-review", "h-rev")]);
        var documents = new Dictionary<string, string>(StringComparer.Ordinal) { ["standing_instructions"] = "Run dotnet test." };

        await starter.StartAsync(
            new WorkflowRunStartOptions { Process = "manufacture", CorrelationKey = "k1", Documents = documents, StartedBy = TestPrincipals.Starter },
            CancellationToken.None);

        store.StartedRuns.Single().Manifest!.Documents["standing_instructions"].Should().Be("Run dotnet test.");
    }

    [Fact]
    public async Task A_process_with_no_active_version_fails_naming_the_process()
    {
        var definitions = new InMemoryProcessDefinitionStore(); // nothing seeded — "manufacture" has no active version
        var store = new FakeWorkflowStore(definitions);
        var resolver = new CatalogRunManifestResolver(
            new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal)),
            new FakeAgentCatalog([]),
            new InMemorySkillStore(Clock));
        var starter = new WorkflowRunStarter(definitions, resolver, store);

        var started = await starter.StartAsync(
            new WorkflowRunStartOptions { Process = "manufacture", CorrelationKey = "k1", StartedBy = TestPrincipals.Starter },
            CancellationToken.None);

        started.IsFailure.Should().BeTrue();
        started.Error.Should().Contain("manufacture").And.Contain("no active version", "GetAsync would fail with a similarly-shaped 'manufacture'-naming message too — the wording must show this is the version check, not a fallback to that other failure path");
        store.StartedRuns.Should().BeEmpty();
    }

    /// <summary>
    /// A variables bag over <c>WorkflowVariableBlock</c>'s key cap makes <see cref="FakeWorkflowStore.StartAsync(WorkflowStartRequest,CancellationToken)"/>
    /// return a <see cref="ZeroAlloc.Results.Result{T}"/> failure — the same check <c>OrmWorkflowStore</c>
    /// carries — which <see cref="WorkflowRunStarter.StartAsync"/> now simply passes through: it is a caller
    /// input problem the store's own <c>Result</c> channel already names, not an infrastructure fault, and not
    /// something this method needs to catch and translate.
    /// </summary>
    [Fact]
    public async Task An_over_cap_variables_bag_is_returned_as_a_failure_not_thrown()
    {
        var (_, starter) = await StarterWith(
            agents: [Def("implementer"), Def("reviewer")],
            skills: [Skill("manufacture-implement", "h-imp"), Skill("manufacture-review", "h-rev")]);

        var overCap = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < 200; i++)
        {
            overCap[$"key-{i}"] = i;
        }

        var started = await starter.StartAsync(
            new WorkflowRunStartOptions { Process = "manufacture", CorrelationKey = "k1", Variables = overCap, StartedBy = TestPrincipals.Starter },
            CancellationToken.None);

        started.IsFailure.Should().BeTrue("an over-cap bag is a caller input problem the store's own guard already names — it must come back as a Result, not a thrown exception");
    }

    /// <summary>
    /// Proves <see cref="WorkflowRunStarter.StartAsync"/> carries no blanket <c>catch (ArgumentException)</c>
    /// around its call to the store: an exception the store throws — here, standing in for "a missing
    /// <c>StartedBy</c> somehow reaching the store" — propagates out of this method uncaught, rather than being
    /// silently turned into a <see cref="ZeroAlloc.Results.Result{T}"/> failure. <see cref="AlwaysThrowsOnStart"/>
    /// throws regardless of what it is given, so this is a general regression test against the blanket catch
    /// coming back — not a claim that <see cref="WorkflowRunStarter"/> itself can construct a request with a
    /// null <c>StartedBy</c>, which its own guard above already rules out.
    /// </summary>
    [Fact]
    public async Task An_exception_the_store_throws_propagates_and_is_not_converted_into_a_failure()
    {
        var (_, starter) = await StarterWith(
            agents: [Def("implementer"), Def("reviewer")],
            skills: [Skill("manufacture-implement", "h-imp"), Skill("manufacture-review", "h-rev")],
            wrapStore: store => new AlwaysThrowsOnStart(store));

        var act = async () => await starter.StartAsync(
            new WorkflowRunStartOptions { Process = "manufacture", CorrelationKey = "k1", StartedBy = TestPrincipals.Starter },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>("its red is re-adding the blanket catch (ArgumentException), which would turn this into a Result.Failure instead");
    }

    /// <summary>
    /// <see cref="WorkflowRunStartOptions.StartedBy"/> and <see cref="WorkflowRunStartOptions.RunId"/> both reach
    /// the store's <see cref="WorkflowStartRequest"/> unchanged — <see cref="WorkflowRunStarter.StartAsync"/> is a
    /// pass-through for both, not just for <see cref="WorkflowRunStartOptions.Variables"/> and
    /// <see cref="WorkflowRunStartOptions.Documents"/>.
    /// </summary>
    [Fact]
    public async Task The_options_overload_carries_the_starter_and_the_run_id_to_the_store()
    {
        var (store, starter) = await StarterWith(
            agents: [Def("implementer"), Def("reviewer")],
            skills: [Skill("manufacture-implement", "h-imp"), Skill("manufacture-review", "h-rev")]);
        var runId = Guid.NewGuid();

        var started = await starter.StartAsync(
            new WorkflowRunStartOptions { Process = "manufacture", CorrelationKey = "k1", RunId = runId, StartedBy = new RunPrincipal("u", ["admin"]) },
            CancellationToken.None);

        started.Value.Should().Be(runId);
        store.StartedRuns.Single().StartedBy.Id.Should().Be("u");
    }

    /// <summary>
    /// Every start names its starter (ruling R26): refused before the store is ever called, so the fake's own
    /// <see cref="ArgumentNullException"/> guard — which names <c>request.StartedBy</c>, not
    /// <c>options.StartedBy</c> — never fires.
    /// </summary>
    [Fact]
    public async Task A_start_with_no_starter_is_refused_before_the_store_is_called()
    {
        var (store, starter) = await StarterWith(
            agents: [Def("implementer"), Def("reviewer")],
            skills: [Skill("manufacture-implement", "h-imp"), Skill("manufacture-review", "h-rev")]);

        var act = async () => await starter.StartAsync(
            new WorkflowRunStartOptions { Process = "manufacture", CorrelationKey = "k2", StartedBy = null! },
            CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentNullException>()).WithParameterName("options.StartedBy", "the starter refuses it before the store sees it");
        store.StartedRuns.Should().BeEmpty();
    }

    /// <summary>
    /// Wires a resolvable two-node <c>manufacture</c> process — every agent named in <see cref="TwoNodeProcessYaml"/>
    /// resolves against <paramref name="agents"/>, and every skill it names is upserted from <paramref name="skills"/>
    /// — behind a <see cref="WorkflowRunStarter"/>. <paramref name="wrapStore"/> lets a test substitute the
    /// store the starter itself talks to (see <see cref="AlwaysThrowsOnStart"/>) while the returned
    /// <see cref="FakeWorkflowStore"/> stays the plain one, so assertions such as <c>store.StartedRuns</c> keep
    /// working unwrapped.
    /// </summary>
    private static async Task<(FakeWorkflowStore Store, WorkflowRunStarter Starter)> StarterWith(
        IReadOnlyList<AgentDefinition> agents, IReadOnlyList<SkillDocument> skills, Func<FakeWorkflowStore, IWorkflowStore>? wrapStore = null)
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(TwoNodeProcessYaml);
        var store = new FakeWorkflowStore(definitions);

        var byName = new Dictionary<string, AgentId>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            byName[agent.Name] = agent.Id;
        }

        var references = new FakeWorkflowReferenceResolver(byName);
        var catalog = new FakeAgentCatalog(agents);
        var skillStore = new InMemorySkillStore(Clock);
        foreach (var skill in skills)
        {
            await skillStore.UpsertAsync(skill, CancellationToken.None);
        }

        var resolver = new CatalogRunManifestResolver(references, catalog, skillStore);
        var starter = new WorkflowRunStarter(definitions, resolver, wrapStore is null ? store : wrapStore(store));
        return (store, starter);
    }

    private static AgentDefinition Def(string name, string? revision = null) =>
        new() { Id = AgentId.New(), Name = name, Instructions = "do the thing", Revision = revision };

    private static SkillDocument Skill(string name, string hash) => new()
    {
        Name = SkillName.Parse(name),
        Description = "a test skill",
        Body = "do the thing",
        SourcePath = $"{name}/SKILL.md",
        ContentHash = hash,
        UpdatedAt = Clock.GetUtcNow(),
    };

    /// <summary>
    /// Delegates every call to <paramref name="inner"/> except <see cref="StartAsync"/>, which always throws —
    /// standing in for a store rejecting a request for a reason of its own, so
    /// <see cref="An_exception_the_store_throws_propagates_and_is_not_converted_into_a_failure"/> can prove
    /// <see cref="WorkflowRunStarter.StartAsync"/> carries no blanket catch around the call to the store.
    /// </summary>
    private sealed class AlwaysThrowsOnStart(IWorkflowStore inner) : IWorkflowStore
    {
        public ValueTask<Result<Guid>> StartAsync(WorkflowStartRequest request, CancellationToken ct) =>
            throw new ArgumentNullException(nameof(request), "simulating a missing StartedBy reaching the store");

        public ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct) => inner.FindAsync(runId, ct);

        public ValueTask CompleteNodeAsync(Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct) =>
            inner.CompleteNodeAsync(runId, seq, transition, result, ct);

        public ValueTask<Result> ResumeAsync(Guid runId, WorkflowResumeRequest request, CancellationToken ct) =>
            inner.ResumeAsync(runId, request, ct);

        public ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct) =>
            inner.FailAsync(runId, errorMessage, ct);

        public ValueTask<bool> FailStrandedAsync(Guid runId, long expectedSeq, string errorMessage, CancellationToken ct) =>
            inner.FailStrandedAsync(runId, expectedSeq, errorMessage, ct);

        public ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct) =>
            inner.CancelAsync(runId, reason, ct);

        public ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct) =>
            inner.FindStrandedAsync(olderThan, ct);
    }
}
