using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Thalos.Memory;
using Thalos.Runtime;
using Thalos.Skills;
using Thalos.Testing;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Skills;

/// <summary>
/// The whole feature through a real <see cref="IHost"/> and a real turn: files on disk are synced at host start, the catalogue
/// lands in the instructions the model is given, the model calls <c>skills__load</c> and the wrapped body comes back into the
/// conversation. Everything else in this project tests a unit or a host in isolation; these are the acceptance facts.
/// </summary>
public sealed class SkillEndToEndTests
{
    private sealed class TestCaller(string id) : ISecurityContext
    {
        public string Id { get; } = id;

        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static AgentDefinition Agent(IReadOnlyList<string> skills, IReadOnlyList<string>? tools = null) =>
        new() { Id = AgentId.New(), Name = "a", Instructions = "You are helpful.", Skills = skills, Tools = tools ?? ["skills__*"] };

    /// <summary>A production-shaped host over a scripted model: hosted services run, so the file sync is the real one.</summary>
    private static async Task<(IHost Host, ScriptedChatClient Client)> StartAsync(AgentDefinition agent, string root, Action<ThalosBuilder>? extra = null)
    {
        var client = new ScriptedChatClient();
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake");
        provider.DefaultModel.Returns("m");
        provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(client);

        var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashedBagOfWordsEmbeddingGenerator(512));
            services.AddThalos(t =>
            {
                t.UseChatClientProvider(provider).UseInMemorySessionStore().UseSkills(o => o.Roots.Add(root)).AddAgent(agent);
                extra?.Invoke(t);
            });
        }).Build();

        await host.StartAsync(CancellationToken.None);
        return (host, client);
    }

    private static async Task<Result<AgentTurnResult, AgentError>> RunAsync(IHost host, AgentDefinition agent, ISecurityContext caller, string text)
    {
        var runtime = host.Services.GetRequiredService<IAgentRuntime>();
        var session = (await runtime.CreateSessionAsync(agent.Id, caller, CancellationToken.None)).Value;
        return await runtime.RunTurnAsync(new AgentTurnRequest(session, text, caller), CancellationToken.None);
    }

    /// <summary>MAF 1.17.0 delivers AIContext.Instructions as ChatOptions.Instructions; a system message is accepted too.</summary>
    private static string AllInstructions((IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) request) =>
        (request.Options?.Instructions ?? "") + "\n" + string.Join('\n', request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Every function result the model was shown, in order — what the tool actually put back into the conversation.</summary>
    private static List<string> ToolResults((IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) request) =>
        request.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.Result?.ToString() ?? "").ToList();

    [Fact]
    public async Task The_catalogue_reaches_the_model_and_only_lists_the_agents_skills()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut and publish a release.");
        folder.WriteFolderSkill("dotnet-migrations", "How to add an EF Core migration.");
        var agent = Agent(["dotnet-*"]);
        var (host, client) = await StartAsync(agent, folder.Root);
        using var _ = host;
        client.ThenText("Sure.");

        var result = await RunAsync(host, agent, new TestCaller("alice"), "How do I add a migration?");

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : "");
        var instructions = AllInstructions(client.Requests.Single());
        instructions.Should().Contain("<skills note=").And.Contain("- dotnet-migrations: How to add an EF Core migration.");
        instructions.Should().NotContain("- release:", "the agent's globs decide what it sees");
    }

    /// <summary>
    /// The catalogue is <em>appended</em>: the agent's own instructions come first and survive intact. A tool call means two
    /// model round-trips, and the catalogue must be in both — the second request is where the model decides what to do with the
    /// skill it just loaded — but exactly once in each.
    /// </summary>
    [Fact]
    public async Task The_catalogue_follows_the_agents_own_instructions_and_appears_once_in_every_round_trip()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut a release.", body: "# Releasing\n1. Tag it.");
        var agent = Agent(["*"]);
        var (host, client) = await StartAsync(agent, folder.Root);
        using var _ = host;
        client.ThenToolCall("skills__load", new { name = "release" }).ThenText("Following the release procedure.");

        (await RunAsync(host, agent, new TestCaller("alice"), "Cut a release.")).IsSuccess.Should().BeTrue();

        client.Requests.Should().HaveCount(2, "a tool call costs a second round-trip");
        foreach (var request in client.Requests)
        {
            var instructions = AllInstructions(request);
            instructions.Should().Contain("You are helpful.", "the catalogue must never replace the agent's own instructions").And.Contain("<skills note=");
            instructions.IndexOf("You are helpful.", StringComparison.Ordinal).Should()
                .BeLessThan(instructions.IndexOf("<skills note=", StringComparison.Ordinal), "the catalogue is appended after them");
            Occurrences(instructions, "<skills note=").Should().Be(1, "one catalogue per request, never accumulated");
            Occurrences(instructions, "- release: How we cut a release.").Should().Be(1);
        }
    }

    [Fact]
    public async Task The_model_can_call_skills__load_and_the_wrapped_body_reaches_the_conversation()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut a release.", body: "# Releasing\n1. Tag it.\n2. Push it.");
        var agent = Agent(["*"]);
        var (host, client) = await StartAsync(agent, folder.Root);
        using var _ = host;
        client.ThenToolCall("skills__load", new { name = "release" }).ThenText("Following the release procedure.");

        var result = await RunAsync(host, agent, new TestCaller("alice"), "Cut a release.");

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : "");
        var call = result.Value.ToolCalls.Should().ContainSingle().Which;
        call.ToolName.Should().Be("skills__load");
        call.Succeeded.Should().BeTrue();
        call.ResultPreview.Should().Contain("<skill name=\"release\">").And.Contain("1. Tag it.");

        // The audit preview is not what the model saw: assert the function result carried back into the second request.
        ToolResults(client.Requests[1]).Should().ContainSingle().Which.Should()
            .StartWith("<skill name=\"release\">").And.Contain("2. Push it.").And.EndWith("</skill>");
        result.Value.Text.Should().Be("Following the release procedure.");
    }

    [Fact]
    public async Task The_model_can_call_skills__search_and_gets_ranked_names_without_bodies()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut and publish a release.", body: "SECRET-BODY-TOKEN");
        var agent = Agent(["*"]);
        var (host, client) = await StartAsync(agent, folder.Root);
        using var _ = host;
        client.ThenToolCall("skills__search", new { query = "cut and publish a release" }).ThenText("Found it.");

        var result = await RunAsync(host, agent, new TestCaller("alice"), "What do we have about releases?");

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : "");
        result.Value.ToolCalls.Should().ContainSingle().Which.ToolName.Should().Be("skills__search");
        var answer = ToolResults(client.Requests[1]).Should().ContainSingle().Which;
        answer.Should().Contain("- release: How we cut and publish a release.").And.NotContain("SECRET-BODY-TOKEN");
    }

    [Fact]
    public async Task An_agent_without_skill_globs_gets_no_block_but_keeps_the_tools()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut a release.");
        var agent = Agent([]);
        var (host, client) = await StartAsync(agent, folder.Root);
        using var _ = host;
        client.ThenText("ok");

        (await RunAsync(host, agent, new TestCaller("alice"), "anything")).IsSuccess.Should().BeTrue();

        AllInstructions(client.Requests.Single()).Should().NotContain("<skills note=");
        client.Requests.Single().Options!.Tools.Should().Contain(t => t.Name == "skills__load");
    }

    [Fact]
    public async Task Loading_a_skill_outside_the_globs_answers_unknown_and_the_turn_still_succeeds()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut a release.", body: "SECRET-BODY-TOKEN");
        var agent = Agent(["dotnet-*"]);
        var (host, client) = await StartAsync(agent, folder.Root);
        using var _ = host;
        client.ThenToolCall("skills__load", new { name = "release" }).ThenText("I do not have that procedure.");

        var result = await RunAsync(host, agent, new TestCaller("alice"), "Cut a release.");

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : "");
        result.Value.ToolCalls.Should().ContainSingle().Which.ResultPreview.Should().StartWith("Unknown skill");
        ToolResults(client.Requests[1]).Should().ContainSingle().Which.Should().NotContain("SECRET-BODY-TOKEN");
    }

    /// <summary>
    /// The genuine both-packages composition fact Task 17 could only stand in for. Memory and skills are independent packages
    /// that meet only in <see cref="ChatOptions.Instructions"/>; both blocks must arrive whole, in context-provider
    /// registration order, with the agent's own instructions still first.
    /// </summary>
    [Fact]
    public async Task Memory_and_skills_both_reach_the_instructions_and_neither_clobbers_the_other()
    {
        using var folder = new SkillFolder();
        folder.WriteFolderSkill("release", "How we cut and publish a release.");
        var agent = Agent(["*"], tools: ["*"]);
        var (host, client) = await StartAsync(agent, folder.Root, t => t.UseMemory(o => o.Recall.MinScore = 0.1));
        using var _ = host;
        await host.Services.GetRequiredService<IMemoryService>().RememberAsync(
            new RememberRequest { OwnerId = "alice", Text = "The user prefers xUnit over NUnit.", Kind = MemoryKind.Preference },
            CancellationToken.None);
        client.ThenText("ok");

        var result = await RunAsync(host, agent, new TestCaller("alice"), "xUnit or NUnit when we cut a release?");

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : "");
        var instructions = AllInstructions(client.Requests.Single());
        instructions.Should()
            .Contain("You are helpful.")
            .And.Contain("<skills note=").And.Contain("- release: How we cut and publish a release.")
            .And.Contain("<memories note=").And.Contain("The user prefers xUnit over NUnit.");
        var own = instructions.IndexOf("You are helpful.", StringComparison.Ordinal);
        var skills = instructions.IndexOf("<skills note=", StringComparison.Ordinal);
        var memories = instructions.IndexOf("<memories note=", StringComparison.Ordinal);
        own.Should().BeLessThan(skills).And.BeLessThan(memories);
        skills.Should().BeLessThan(memories, "context providers contribute in registration order: UseSkills ran before UseMemory");
        client.Requests.Single().Options!.Tools.Should().Contain(t => t.Name == "skills__load").And.Contain(t => t.Name == "memory__recall");
    }
}
