using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Thalos.Runtime;
using Thalos.Skills;
using Thalos.Testing;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Skills;

public sealed class SkillContextProviderTests
{
    private sealed class TestCaller(string id) : ISecurityContext
    {
        public string Id { get; } = id;
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static ChatClientAgent Agent() => new(new ScriptedChatClient(), new ChatClientAgentOptions { Name = "a" });

#pragma warning disable MAAI001 // InvokingContext is [Experimental] in MAF 1.17.0; tests build it directly to drive InvokingAsync
    private static AIContextProvider.InvokingContext Context() =>
        new(Agent(), null!, new AIContext { Messages = [new ChatMessage(ChatRole.User, "how do we release?")] });
#pragma warning restore MAAI001

    private static SkillCatalogue Loaded()
    {
        var catalogue = new SkillCatalogue();
        catalogue.Set([SkillModelTests.Doc("release", "How we cut a release.")], maxChars: 2000);
        return catalogue;
    }

    [Fact]
    public async Task The_catalogue_is_injected_as_instructions()
    {
        var provider = new SkillContextProvider(Loaded(), ["*"], new AgentEventHub());
        var context = await provider.InvokingAsync(Context(), CancellationToken.None);
        context.Instructions.Should().StartWith("<skills note=").And.Contain("- release: How we cut a release.");
    }

    [Fact]
    public async Task An_agent_with_no_matching_skills_adds_nothing()
    {
        var provider = new SkillContextProvider(Loaded(), ["nothing-*"], new AgentEventHub());
        (await provider.InvokingAsync(Context(), CancellationToken.None)).Instructions.Should().BeNull();
    }

    [Fact]
    public async Task The_catalogue_is_injected_for_an_anonymous_caller_too()
    {
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);
        var provider = new SkillContextProvider(Loaded(), ["*"], new AgentEventHub());
        (await provider.InvokingAsync(Context(), CancellationToken.None)).Instructions.Should().Contain("- release:");
    }

    [Fact]
    public async Task A_failing_catalogue_never_fails_the_turn_and_raises_the_event()
    {
        var hub = new AgentEventHub();
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice"));
        var provider = new SkillContextProvider(new ThrowingCatalogue(), ["*"], hub);

        var context = await provider.InvokingAsync(Context(), CancellationToken.None);

        context.Instructions.Should().BeNull("a broken catalogue must not fail the turn");
        var published = new List<AgentEvent>();
        while (scope.Events.TryRead(out var e))
        {
            published.Add(e);
        }

        published.OfType<SkillCatalogueFailedEvent>().Should().ContainSingle().Which.Code.Should().Be(AgentErrorCode.SkillStoreFailed);
    }

    /// <summary>Outside a turn there is no scope channel, so the hub is the only route — and it must actually deliver.</summary>
    [Fact]
    public async Task Outside_a_turn_the_failure_reaches_a_hub_subscriber()
    {
        var hub = new AgentEventHub();
        var received = new List<AgentEvent>();
        using var subscription = hub.Subscribe((e, _) => { received.Add(e); return default; });
        var provider = new SkillContextProvider(new ThrowingCatalogue(), ["*"], hub);

        (await provider.InvokingAsync(Context(), CancellationToken.None)).Instructions.Should().BeNull();

        var failed = received.Should().ContainSingle().Which.Should().BeOfType<SkillCatalogueFailedEvent>().Subject;
        failed.Code.Should().Be(AgentErrorCode.SkillStoreFailed);
        failed.Kind.Should().Be(AgentEventKinds.SkillCatalogueFailed);
    }

    /// <summary>The recovery path must not itself be able to fail the turn: a subscriber that throws is the hub's problem, not ours.</summary>
    [Fact]
    public async Task A_throwing_event_subscriber_still_cannot_fail_the_turn()
    {
        var hub = new AgentEventHub();
        using var subscription = hub.Subscribe((_, _) => throw new InvalidOperationException("subscriber down"));
        var provider = new SkillContextProvider(new ThrowingCatalogue(), ["*"], hub);

        (await provider.InvokingAsync(Context(), CancellationToken.None)).Instructions.Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_turn_is_not_swallowed_as_a_catalogue_failure()
    {
        var hub = new AgentEventHub();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var provider = new SkillContextProvider(new CancellingCatalogue(), ["*"], hub);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.InvokingAsync(Context(), cts.Token).AsTask());

        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice"));
        (await provider.InvokingAsync(Context(), CancellationToken.None)).Instructions.Should().BeNull("the same exception with a live token is an ordinary failure");
        scope.Events.TryRead(out var e).Should().BeTrue();
        e.Should().BeOfType<SkillCatalogueFailedEvent>();
    }

    [Fact]
    public void The_source_creates_a_provider_only_for_an_agent_with_globs_and_only_when_enabled()
    {
        var catalogue = Loaded();
        var source = new SkillContextProviderSource(catalogue, Options.Create(new SkillOptions()), new AgentEventHub());
        var bare = new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "i" };

        source.CreateProvider(bare).Should().BeNull("skills are opt-in per agent");
        source.CreateProvider(bare with { Skills = ["*"] }).Should().BeOfType<SkillContextProvider>();

        var off = new SkillContextProviderSource(catalogue, Options.Create(new SkillOptions { Enabled = false }), new AgentEventHub());
        off.CreateProvider(bare with { Skills = ["*"] }).Should().BeNull();
    }

    /// <summary>
    /// The provider is built per agent from that agent's own <c>AgentDefinition.Skills</c>, so two agents sharing one catalogue
    /// must never see each other's skills — the cross-agent leak Task 16 fixed in the cache key.
    /// </summary>
    [Fact]
    public async Task Two_agents_with_different_globs_never_see_each_others_catalogue()
    {
        var catalogue = new SkillCatalogue();
        catalogue.Set(
            [SkillModelTests.Doc("release", "How we cut a release."), SkillModelTests.Doc("dotnet-migrations", "How to add an EF migration.")],
            maxChars: 2000);
        var source = new SkillContextProviderSource(catalogue, Options.Create(new SkillOptions()), new AgentEventHub());
        var bare = new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "i" };

        var releaser = source.CreateProvider(bare with { Id = AgentId.New(), Skills = ["release"] }).Should().BeOfType<SkillContextProvider>().Subject;
        var migrator = source.CreateProvider(bare with { Id = AgentId.New(), Skills = ["dotnet-*"] }).Should().BeOfType<SkillContextProvider>().Subject;

        releaser.Globs.Should().Equal("release");
        migrator.Globs.Should().Equal("dotnet-*");

        var first = (await releaser.InvokingAsync(Context(), CancellationToken.None)).Instructions;
        var second = (await migrator.InvokingAsync(Context(), CancellationToken.None)).Instructions;

        first.Should().Contain("- release:").And.NotContain("dotnet-migrations");
        second.Should().Contain("- dotnet-migrations:").And.NotContain("- release:");
    }

    /// <summary>No globs, globs matching nothing and an empty store must each contribute nothing at all — not an empty block.</summary>
    [Fact]
    public async Task The_three_empty_cases_contribute_nothing()
    {
        var hub = new AgentEventHub();
        var loaded = Loaded();

        (await new SkillContextProvider(loaded, [], hub).InvokingAsync(Context(), CancellationToken.None)).Instructions.Should().BeNull("no globs");
        (await new SkillContextProvider(loaded, ["nothing-*"], hub).InvokingAsync(Context(), CancellationToken.None)).Instructions.Should().BeNull("nothing matches");
        (await new SkillContextProvider(new SkillCatalogue(), ["*"], hub).InvokingAsync(Context(), CancellationToken.None)).Instructions.Should().BeNull("empty store");
    }

    /// <summary>
    /// MAF composes provider instructions with the agent's own; the catalogue must be appended, never a replacement — and an
    /// agent with no skills must not leave a blank line behind. A second provider stands in for Thalos.NET.Memory, which this
    /// test project cannot reference (Skills and Memory are independent packages).
    /// </summary>
    [Fact]
    public async Task The_catalogue_composes_with_the_agents_own_instructions_and_another_provider()
    {
        var client = new ScriptedChatClient().ThenText("ok").ThenText("ok");
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "a",
            AIContextProviders = [new MemoryLikeProvider(), new SkillContextProvider(Loaded(), ["*"], new AgentEventHub())],
            ChatOptions = new ChatOptions { Instructions = "You are helpful." },
        });

        await agent.RunAsync("how do we release?");

        AllInstructions(client.Requests[0]).Should()
            .Contain("You are helpful.").And.Contain("<memories>recalled</memories>").And.Contain("- release: How we cut a release.");

        var bare = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "b",
            AIContextProviders = [new SkillContextProvider(Loaded(), ["nothing-*"], new AgentEventHub())],
            ChatOptions = new ChatOptions { Instructions = "You are helpful." },
        });

        await bare.RunAsync("how do we release?");

        client.Requests[1].Options!.Instructions.Should().Be("You are helpful.", "an agent with no skills adds nothing, not a blank line");
    }

    /// <summary>MAF may deliver AIContext.Instructions as ChatOptions.Instructions (what 1.17.0 does) or as a system message; accept both.</summary>
    private static string AllInstructions((IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) request) =>
        (request.Options?.Instructions ?? "") + "\n" + string.Join('\n', request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));

    private sealed class MemoryLikeProvider : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) =>
            new(new AIContext { Instructions = "<memories>recalled</memories>" });
    }

    /// <summary>A catalogue whose render throws, standing in for a store failure the sync could not repair.</summary>
    private sealed class ThrowingCatalogue : SkillCatalogue
    {
        public override string? Render(IReadOnlyList<string> globs) => throw new InvalidOperationException("no catalogue");
    }

    /// <summary>A catalogue whose render reports cancellation, to pin the exception filter.</summary>
    private sealed class CancellingCatalogue : SkillCatalogue
    {
        public override string? Render(IReadOnlyList<string> globs) => throw new OperationCanceledException("cancelled");
    }
}
