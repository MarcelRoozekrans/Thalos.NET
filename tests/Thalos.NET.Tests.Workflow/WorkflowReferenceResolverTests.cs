using Thalos.Skills;
using Thalos.Workflow;

namespace Thalos.Tests.Workflow;

/// <summary>Tests for <see cref="WorkflowReferenceResolver"/>, the default <see cref="IWorkflowReferenceResolver"/> over Thalos's own <see cref="IAgentCatalog"/> and <see cref="ISkillStore"/>.</summary>
public sealed class WorkflowReferenceResolverTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    [Fact]
    public async Task ResolveAgentIdAsync_matches_case_insensitively()
    {
        var id = AgentId.New();
        var catalog = new FakeAgentCatalog([new AgentDefinition { Id = id, Name = "Builder", Instructions = "build things" }]);
        var resolver = new WorkflowReferenceResolver(catalog, new InMemorySkillStore(Clock));

        var resolved = await resolver.ResolveAgentIdAsync("BUILDER", CancellationToken.None);

        resolved.Should().Be(id, "a process file that validates against a name typed in a different case must resolve the same agent at dispatch time");
    }

    [Fact]
    public async Task ResolveAgentIdAsync_returns_null_for_an_unregistered_name()
    {
        var catalog = new FakeAgentCatalog([new AgentDefinition { Id = AgentId.New(), Name = "Builder", Instructions = "build things" }]);
        var resolver = new WorkflowReferenceResolver(catalog, new InMemorySkillStore(Clock));

        var resolved = await resolver.ResolveAgentIdAsync("ghost-writer", CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task SkillExistsAsync_is_true_for_an_active_skill()
    {
        var store = new InMemorySkillStore(Clock);
        await store.UpsertAsync(Skill("draft"), CancellationToken.None);
        var resolver = new WorkflowReferenceResolver(new FakeAgentCatalog([]), store);

        (await resolver.SkillExistsAsync("draft", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task SkillExistsAsync_is_false_for_an_unregistered_name()
    {
        var resolver = new WorkflowReferenceResolver(new FakeAgentCatalog([]), new InMemorySkillStore(Clock));

        (await resolver.SkillExistsAsync("ghost-skill", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SkillExistsAsync_is_false_for_a_deactivated_skill()
    {
        var store = new InMemorySkillStore(Clock);
        await store.UpsertAsync(Skill("draft"), CancellationToken.None);
        await store.DeactivateMissingAsync([], CancellationToken.None);
        var resolver = new WorkflowReferenceResolver(new FakeAgentCatalog([]), store);

        // The skill's file disappeared from the repository — ISkillStore.GetAsync still returns the row (its
        // own contract says an inactive skill is returned, callers decide), but a process referencing it should
        // fail validation the same as a name that was never registered at all.
        (await resolver.SkillExistsAsync("draft", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SkillExistsAsync_is_false_for_a_name_that_is_not_a_valid_skill_name()
    {
        var resolver = new WorkflowReferenceResolver(new FakeAgentCatalog([]), new InMemorySkillStore(Clock));

        (await resolver.SkillExistsAsync("Not A Valid Name!", CancellationToken.None)).Should().BeFalse();
    }

    private static SkillDocument Skill(string name) => new()
    {
        Name = SkillName.Parse(name),
        Description = "a test skill",
        Body = "do the thing",
        SourcePath = $"{name}/SKILL.md",
        ContentHash = "hash",
        UpdatedAt = Clock.GetUtcNow(),
    };
}
