using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Thalos.Memory;
using Thalos.Runtime;
using Thalos.Testing;

namespace Thalos.Tests.Memory;

public sealed class MemoryEndToEndTests
{
    private static (ServiceProvider sp, ScriptedChatClient client, AgentDefinition agent) Build(AgentMemorySettings? memory = null)
    {
        var client = new ScriptedChatClient();
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake"); provider.DefaultModel.Returns("m");
        provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(client);
        var agent = new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "You are helpful.", Memory = memory };
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashedBagOfWordsEmbeddingGenerator());
        services.AddThalos(t => t.UseChatClientProvider(provider).UseInMemorySessionStore().UseMemory(o => o.Recall.MinScore = 0.1).AddAgent(agent));
        return (services.BuildServiceProvider(), client, agent);
    }

    private static string AllInstructions((IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) request) =>
        (request.Options?.Instructions ?? "") + "\n" + string.Join('\n', request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));

    [Fact]
    public async Task Auto_recall_injects_the_callers_memories_and_streams_MemoryRecalled()
    {
        var (sp, client, agent) = Build();
        var caller = new TestCaller("alice");
        await sp.GetRequiredService<IMemoryService>().RememberAsync(new RememberRequest { OwnerId = "alice", Text = "The user prefers xUnit over NUnit.", Kind = MemoryKind.Preference }, default);
        await sp.GetRequiredService<IMemoryService>().RememberAsync(new RememberRequest { OwnerId = "bob", Text = "Bob prefers NUnit over xUnit." }, default);
        client.ThenText("xUnit it is.");
        var runtime = sp.GetRequiredService<IAgentRuntime>();
        var s = (await runtime.CreateSessionAsync(agent.Id, caller, default)).Value;

        var events = new List<AgentEvent>();
        await foreach (var e in runtime.RunTurnStreamingAsync(new AgentTurnRequest(s, "xUnit or NUnit for the new tests?", caller), default))
        {
            events.Add(e);
        }

        events.OfType<TurnCompletedEvent>().Should().ContainSingle();
        events.OfType<MemoryRecalledEvent>().Should().ContainSingle().Which.Count.Should().Be(1);
        var instructions = AllInstructions(client.Requests.Single());
        instructions.Should().Contain("<memories").And.Contain("[preference · just now] The user prefers xUnit over NUnit.").And.NotContain("Bob prefers");
    }

    [Fact]
    public async Task The_model_can_call_memory__remember_and_the_record_lands_under_the_caller()
    {
        var (sp, client, agent) = Build();
        var caller = new TestCaller("alice");
        client.ThenToolCall("memory__remember", new { text = "The user's project is Daedalus.", kind = "fact" }).ThenText("Noted.");
        var runtime = sp.GetRequiredService<IAgentRuntime>();
        var s = (await runtime.CreateSessionAsync(agent.Id, caller, default)).Value;

        var r = await runtime.RunTurnAsync(new AgentTurnRequest(s, "Remember that my project is Daedalus.", caller), default);

        r.IsSuccess.Should().BeTrue(r.IsFailure ? r.Error.ToString() : "");
        r.Value.ToolCalls.Should().ContainSingle(c => c.ToolName == "memory__remember" && c.Succeeded && c.ResultPreview!.StartsWith("Remembered"));
        var page = (await sp.GetRequiredService<IMemoryStore>().ListAsync(new MemoryQuery { OwnerIds = ["alice"] }, default)).Value;
        page.Items.Should().ContainSingle().Which.Source.Should().Be("tool:memory__remember");
    }

    [Fact]
    public async Task Per_agent_disable_skips_auto_recall_but_tools_still_resolve()
    {
        var (sp, client, agent) = Build(new AgentMemorySettings { Enabled = false });
        var caller = new TestCaller("alice");
        await sp.GetRequiredService<IMemoryService>().RememberAsync(new RememberRequest { OwnerId = "alice", Text = "secret sauce" }, default);
        client.ThenText("ok");
        var runtime = sp.GetRequiredService<IAgentRuntime>();
        var s = (await runtime.CreateSessionAsync(agent.Id, caller, default)).Value;

        await runtime.RunTurnAsync(new AgentTurnRequest(s, "secret sauce?", caller), default);

        AllInstructions(client.Requests.Single()).Should().NotContain("<memories");
        client.Requests.Single().Options!.Tools.Should().Contain(t => t.Name == "memory__recall");
    }
}
