using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Thalos.Memory;
using Thalos.Runtime;
using Thalos.Testing;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Memory;

public sealed class MemoryContextProviderTests
{
    internal static AIAgent Agent() => new ChatClientAgent(new ScriptedChatClient(), new ChatClientAgentOptions { Name = "a" });

#pragma warning disable MAAI001 // the InvokingContext ctor is [Experimental] in MAF 1.17.0; tests build it directly to drive InvokingAsync
    internal static AIContextProvider.InvokingContext Invoking(string userText) =>
        new(Agent(), null!, new AIContext { Messages = [new ChatMessage(ChatRole.User, userText)] });
#pragma warning restore MAAI001

    internal static MemoryContextProvider Provider(MemoryServiceFixture f, AgentId agent, IUntrustedContentScanner? scanner = null, RecallOptions? recall = null) =>
        new(f.Build(), agent, recall ?? new RecallOptions { MinScore = 0.1 }, f.Options.SharedOwnerId, f.Clock, f.Hub, scanner);

    [Fact]
    public async Task Injects_the_block_for_the_last_user_message_and_publishes_MemoryRecalled()
    {
        var f = new MemoryServiceFixture();
        var agent = AgentId.New();
        var stored = (await f.Build().RememberAsync(MemoryServiceFixture.Remember("The user prefers xUnit over NUnit."), default)).Value;
        var provider = Provider(f, agent);
        var s = SessionId.New(); var t = TurnId.New();
        using var scope = TurnScope.Begin(s, t, new TestCaller("alice"), agent);

        var ctx = await provider.InvokingAsync(Invoking("Which test framework does the user like, xUnit or NUnit?"), default);

        ctx.Instructions.Should().StartWith("<memories note=").And.Contain("[fact · just now] The user prefers xUnit over NUnit.").And.EndWith("</memories>");
        scope.Events.TryRead(out var evt).Should().BeTrue();
        var recalled = evt.Should().BeOfType<MemoryRecalledEvent>().Subject;
        recalled.MemoryIds.Should().Equal(stored.Id);
        recalled.Chars.Should().Be(ctx.Instructions!.Length);
        recalled.SessionId.Should().Be(s);
    }

    [Fact]
    public async Task No_hits_no_turn_or_anonymous_caller_yield_no_instructions()
    {
        var f = new MemoryServiceFixture();
        var agent = AgentId.New();
        var provider = Provider(f, agent);

        (await provider.InvokingAsync(Invoking("anything"), default)).Instructions.Should().BeNull("no turn scope");

        using (TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance, agent))
        {
            (await provider.InvokingAsync(Invoking("anything"), default)).Instructions.Should().BeNull("anonymous");
        }

        using (TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice"), agent))
        {
            (await provider.InvokingAsync(Invoking("nothing stored yet"), default)).Instructions.Should().BeNull("empty result");
        }
    }

    [Fact]
    public async Task Scope_is_owner_agent_and_shared_owner()
    {
        var f = new MemoryServiceFixture();
        f.Options.SharedOwnerId = "daedalus";
        var agent = AgentId.New();
        var svc = f.Build();
        await svc.RememberAsync(MemoryServiceFixture.Remember("project rule: use data-testid", owner: "daedalus"), default);
        await svc.RememberAsync(MemoryServiceFixture.Remember("bob rule: use data-testid", owner: "bob"), default);
        await svc.RememberAsync(MemoryServiceFixture.Remember("other agent rule: use data-testid", agent: AgentId.New()), default);
        var provider = Provider(f, agent);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice"), agent);

        var ctx = await provider.InvokingAsync(Invoking("rule for data-testid?"), default);

        ctx.Instructions.Should().Contain("project rule").And.NotContain("bob rule").And.NotContain("other agent rule");
    }
}
