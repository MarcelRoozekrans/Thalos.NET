using Thalos.Memory;
using Thalos.Runtime;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Memory;

/// <summary>
/// <see cref="IMemoryOwner"/>: a caller whose authorization id is per-run (e.g. a workflow run) can report a stable
/// memory owner instead, and force every memory it writes to be agent-pinned regardless of <c>shared</c>.
/// </summary>
public sealed class MemoryOwnerTests
{
    [Fact]
    public async Task Owner_caller_stores_under_MemoryOwnerId_not_under_the_security_context_id()
    {
        var (f, source) = MemoryToolsTests.Build();
        var remember = await MemoryToolsTests.Tool(source, "remember");
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestOwnerCaller("workflow:review:run-1", "role:reviewer"));

        (await remember.InvokeAsync(MemoryToolsTests.Args(("text", "reviewers prefer terse feedback"))))!.ToString().Should().StartWith("Remembered ");

        (await f.Store.ListAsync(new MemoryQuery { OwnerIds = ["role:reviewer"] }, default)).Value.Items.Should().ContainSingle();
        (await f.Store.ListAsync(new MemoryQuery { OwnerIds = ["workflow:review:run-1"] }, default)).Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_caller_with_the_same_MemoryOwnerId_recalls_what_the_first_wrote()
    {
        var (_, source) = MemoryToolsTests.Build(o => o.Recall.MinScore = 0.1);
        var remember = await MemoryToolsTests.Tool(source, "remember");
        var recall = await MemoryToolsTests.Tool(source, "recall");
        using (TurnScope.Begin(SessionId.New(), TurnId.New(), new TestOwnerCaller("workflow:review:run-1", "role:reviewer")))
        {
            (await remember.InvokeAsync(MemoryToolsTests.Args(("text", "deploy notes: use blue green"))))!.ToString().Should().StartWith("Remembered ");
        }

        // a different run of the same role — a different authorization id, the same stable owner
        using var second = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestOwnerCaller("workflow:review:run-2", "role:reviewer"));
        var text = (await recall.InvokeAsync(MemoryToolsTests.Args(("query", "deploy blue green"))))!.ToString()!;

        text.Should().Contain("blue green");
    }

    [Fact]
    public async Task PinMemoriesToAgent_pins_the_stored_record_even_when_shared_is_true()
    {
        var (f, source) = MemoryToolsTests.Build();
        var remember = await MemoryToolsTests.Tool(source, "remember");
        var agent = AgentId.New();
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestOwnerCaller("workflow:review:run-1", "role:reviewer", pinMemoriesToAgent: true), agent);

        (await remember.InvokeAsync(MemoryToolsTests.Args(("text", "pin me"), ("shared", true))))!.ToString().Should().StartWith("Remembered ");

        var stored = (await f.Store.ListAsync(new MemoryQuery { OwnerIds = ["role:reviewer"] }, default)).Value.Items.Single();
        stored.AgentId.Should().Be(agent);
    }

    [Fact]
    public async Task A_caller_that_does_not_implement_IMemoryOwner_is_unaffected()
    {
        var (f, source) = MemoryToolsTests.Build();
        var remember = await MemoryToolsTests.Tool(source, "remember");
        var agent = AgentId.New();
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice"), agent);

        (await remember.InvokeAsync(MemoryToolsTests.Args(("text", "shared by default"))))!.ToString().Should().StartWith("Remembered ");
        (await remember.InvokeAsync(MemoryToolsTests.Args(("text", "pinned on request"), ("shared", false))))!.ToString().Should().StartWith("Remembered ");

        var all = (await f.Store.ListAsync(new MemoryQuery { OwnerIds = ["alice"] }, default)).Value.Items;
        all.Should().HaveCount(2);
        all.Single(r => string.Equals(r.Text, "shared by default", StringComparison.Ordinal)).AgentId.Should().BeNull();
        all.Single(r => string.Equals(r.Text, "pinned on request", StringComparison.Ordinal)).AgentId.Should().Be(agent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_MemoryOwnerId_falls_back_to_the_security_context_id(string blankOwnerId)
    {
        var (f, source) = MemoryToolsTests.Build();
        var remember = await MemoryToolsTests.Tool(source, "remember");
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestOwnerCaller("workflow:review:run-1", blankOwnerId));

        (await remember.InvokeAsync(MemoryToolsTests.Args(("text", "falls back"))))!.ToString().Should().StartWith("Remembered ");

        (await f.Store.ListAsync(new MemoryQuery { OwnerIds = ["workflow:review:run-1"] }, default)).Value.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Anonymous_MemoryOwnerId_falls_back_to_the_security_context_id()
    {
        var (f, source) = MemoryToolsTests.Build();
        var remember = await MemoryToolsTests.Tool(source, "remember");
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestOwnerCaller("workflow:review:run-1", AnonymousSecurityContext.AnonymousId));

        (await remember.InvokeAsync(MemoryToolsTests.Args(("text", "falls back"))))!.ToString().Should().StartWith("Remembered ");

        (await f.Store.ListAsync(new MemoryQuery { OwnerIds = ["workflow:review:run-1"] }, default)).Value.Items.Should().ContainSingle();
    }
}
