using Thalos;
using Thalos.Skills;
using Thalos.Workflow;

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
    /// throw <see cref="ArgumentException"/> — the same guard <c>OrmWorkflowStore</c> carries. That is a caller
    /// input problem, not an infrastructure fault, so <see cref="WorkflowRunStarter.StartAsync"/> must catch it
    /// and hand it back through the <see cref="ZeroAlloc.Results.Result{T}"/> channel every other failure here
    /// already uses, rather than letting it escape as a throw.
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

    private static async Task<(FakeWorkflowStore Store, WorkflowRunStarter Starter)> StarterWith(IReadOnlyList<AgentDefinition> agents, IReadOnlyList<SkillDocument> skills)
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
        var starter = new WorkflowRunStarter(definitions, resolver, store);
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
}
