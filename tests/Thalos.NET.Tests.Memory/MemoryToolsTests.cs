using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Thalos.Memory;
using Thalos.Runtime;
using Thalos.Testing;
using Thalos.Tools;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Memory;

public sealed class MemoryToolsTests
{
    private static readonly string[] TestingTags = ["testing"];

    internal static (MemoryServiceFixture f, MemoryToolSource source) Build(Action<MemoryOptions>? configure = null)
    {
        var f = new MemoryServiceFixture();
        configure?.Invoke(f.Options);
        var services = new ServiceCollection()
            .AddSingleton<IMemoryService>(f.Build())
            .AddSingleton(Options.Create(f.Options))
            .BuildServiceProvider();
        return (f, new MemoryToolSource(services, Options.Create(f.Options)));
    }

    internal static async Task<AIFunction> Tool(MemoryToolSource source, string name) =>
        (AIFunction)(await source.GetToolsAsync(default)).Value.Single(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    internal static AIFunctionArguments Args(params (string Key, object? Value)[] args)
    {
        var a = new AIFunctionArguments(StringComparer.Ordinal);
        foreach (var (k, v) in args)
        {
            a[k] = v;
        }

        return a;
    }

    [Fact]
    public async Task Source_is_named_memory_and_exposes_four_tools()
    {
        var (_, source) = Build();
        source.Name.Should().Be("memory");
        (await source.GetToolsAsync(default)).Value.Select(t => t.Name).Should().BeEquivalentTo(["remember", "recall", "forget", "list"]);
        var (_, hidden) = Build(o => o.ExposeTools = false);
        (await hidden.GetToolsAsync(default)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Remember_writes_under_the_turn_caller_and_pins_when_not_shared()
    {
        var (f, source) = Build();
        var agent = AgentId.New();
        var remember = await Tool(source, "remember");
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice"), agent);

        var shared = (await remember.InvokeAsync(Args(("text", "The user prefers xUnit."), ("kind", "Preference"), ("tags", TestingTags))))!.ToString();
        var pinned = (await remember.InvokeAsync(Args(("text", "Only this agent: use terse answers."), ("shared", false))))!.ToString();

        shared.Should().StartWith("Remembered ").And.Contain("preference");
        var all = (await f.Store.ListAsync(new MemoryQuery { OwnerIds = ["alice"] }, default)).Value.Items;
        all.Should().HaveCount(2);
        all.Single(r => r.Text.StartsWith("The user", StringComparison.Ordinal)).AgentId.Should().BeNull();
        all.Single(r => r.Text.StartsWith("Only this", StringComparison.Ordinal)).AgentId.Should().Be(agent);
        all.Should().OnlyContain(r => r.Source == "tool:memory__remember");
        pinned.Should().StartWith("Remembered ");
    }

    [Fact]
    public async Task Remember_reports_validation_and_unknown_kind_as_text_never_throws()
    {
        var (_, source) = Build();
        var remember = await Tool(source, "remember");
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice"));
        (await remember.InvokeAsync(Args(("text", "  "))))!.ToString().Should().StartWith("Could not remember:");
        (await remember.InvokeAsync(Args(("text", "x"), ("kind", "Not A Kind"))))!.ToString().Should().Contain("unknown kind");
    }

    [Fact]
    public async Task Recall_returns_numbered_lines_with_ids_scoped_to_the_caller()
    {
        // default MinScore (0.6): the bag-of-words generator scores by token overlap, so the query repeats the record's words
        // ("how do we deploy?" would score 0.25, and lowering MinScore lets 128-bucket hash collisions match "nothing about this")
        var (f, source) = Build(o => o.SharedOwnerId = "daedalus");
        var svc = f.Build();
        var mine = (await svc.RememberAsync(MemoryServiceFixture.Remember("deploy with blue green"), default)).Value;
        await svc.RememberAsync(MemoryServiceFixture.Remember("deploy with blue green (project)", owner: "daedalus"), default);
        await svc.RememberAsync(MemoryServiceFixture.Remember("deploy with blue green (bob)", owner: "bob"), default);
        var recall = await Tool(source, "recall");
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice"));

        var text = (await recall.InvokeAsync(Args(("query", "deploy blue green?"), ("topK", 5))))!.ToString()!;

        text.Should().Contain(mine.Id.ToString()).And.Contain("(project)").And.NotContain("(bob)");
        text.Should().StartWith("1. [");
        (await recall.InvokeAsync(Args(("query", "nothing about this"))))!.ToString().Should().Be("No relevant memories.");
    }

    [Fact]
    public async Task Forget_archives_own_memories_only()
    {
        var (f, source) = Build(o => o.SharedOwnerId = "daedalus");
        var svc = f.Build();
        var mine = (await svc.RememberAsync(MemoryServiceFixture.Remember("mine"), default)).Value;
        var project = (await svc.RememberAsync(MemoryServiceFixture.Remember("project", owner: "daedalus"), default)).Value;
        var forget = await Tool(source, "forget");
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice"));

        (await forget.InvokeAsync(Args(("id", mine.Id.ToString()))))!.ToString().Should().Be($"Archived memory {mine.Id}.");
        (await f.Store.GetAsync(mine.Id, default)).Value.IsArchived.Should().BeTrue();
        (await forget.InvokeAsync(Args(("id", project.Id.ToString()))))!.ToString().Should().StartWith("Could not forget:").And.Contain("another owner");
        (await f.Store.GetAsync(project.Id, default)).Value.IsArchived.Should().BeFalse("the shared owner's memory is never touched by the tool");
        (await forget.InvokeAsync(Args(("id", "nope"))))!.ToString().Should().Be("Invalid memory id.");
    }

    [Fact]
    public async Task List_pages_own_and_shared_memories_newest_first()
    {
        var (f, source) = Build(o => o.SharedOwnerId = "daedalus");
        var svc = f.Build();
        await svc.RememberAsync(MemoryServiceFixture.Remember("first", kind: MemoryKind.Note), default);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await svc.RememberAsync(MemoryServiceFixture.Remember("second", kind: MemoryKind.Fact), default);
        await svc.RememberAsync(MemoryServiceFixture.Remember("project fact", owner: "daedalus", kind: MemoryKind.Fact), default);
        await svc.RememberAsync(MemoryServiceFixture.Remember("bobs", owner: "bob"), default);
        var list = await Tool(source, "list");
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice"));

        var all = (await list.InvokeAsync(Args()))!.ToString()!;
        all.Should().StartWith("3 memories (page 1/1)").And.Contain("second").And.Contain("first").And.Contain("project fact").And.NotContain("bobs");
        all.IndexOf("second", StringComparison.Ordinal).Should().BeLessThan(all.IndexOf("first", StringComparison.Ordinal));
        (await list.InvokeAsync(Args(("kind", "note"))))!.ToString().Should().StartWith("1 memories").And.Contain("first");
        (await list.InvokeAsync(Args(("kind", "???"))))!.ToString().Should().Contain("unknown kind");
    }

    [Fact]
    public async Task Anonymous_or_no_turn_is_refused()
    {
        var (_, source) = Build();
        var remember = await Tool(source, "remember");
        (await remember.InvokeAsync(Args(("text", "x"))))!.ToString().Should().Contain("authenticated caller inside an agent turn");
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);
        (await remember.InvokeAsync(Args(("text", "x"))))!.ToString().Should().Contain("authenticated caller inside an agent turn");
    }

    [Fact]
    public async Task Through_the_catalog_denied_calls_never_reach_the_service()
    {
        var (f, source) = Build();
        var authorizer = Substitute.For<IToolAuthorizer>();
        authorizer.AuthorizeAsync(default!, default!, default, default).ReturnsForAnyArgs(ci =>
            string.Equals(ci.Arg<string>(), "memory__forget", StringComparison.Ordinal) ? ToolAuthorizationDecision.Deny("policy") : ToolAuthorizationDecision.Allow());
        var publisher = new RecordingNotificationPublisher();
        var catalog = new ToolCatalog([source], authorizer, publisher, TimeProvider.System);
        var tools = (await catalog.ResolveAsync(new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "i" }, default)).Value.Cast<AIFunction>().ToList();
        tools.Select(t => t.Name).Should().BeEquivalentTo(["memory__remember", "memory__recall", "memory__forget", "memory__list"]);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestCaller("alice"));

        var stored = (await tools.Single(t => string.Equals(t.Name, "memory__remember", StringComparison.Ordinal)).InvokeAsync(Args(("text", "guarded"))))!.ToString();
        var id = (await f.Store.ListAsync(new MemoryQuery { OwnerIds = ["alice"] }, default)).Value.Items.Single().Id;
        var denied = (await tools.Single(t => string.Equals(t.Name, "memory__forget", StringComparison.Ordinal)).InvokeAsync(Args(("id", id.ToString()))))!.ToString();

        stored.Should().StartWith("Remembered");
        denied.Should().StartWith("Tool call denied:");
        (await f.Store.GetAsync(id, default)).Value.IsArchived.Should().BeFalse();
        publisher.Of<ToolCallDeniedNotification>().Should().ContainSingle().Which.ToolName.Should().Be("memory__forget");
    }
}
