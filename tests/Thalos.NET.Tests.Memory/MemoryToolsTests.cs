using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Memory;
using Thalos.Runtime;
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
}
