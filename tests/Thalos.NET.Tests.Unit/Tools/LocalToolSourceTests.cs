using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Tools;

namespace Thalos.Tests.Unit.Tools;

public sealed class LocalToolSourceTests
{
    public sealed class Counter { public int Value { get; set; } }

    [ThalosToolType]
    public sealed class MathTools(Counter counter)
    {
        [ThalosTool("add")]
        [Description("Adds two integers")]
        public int Add([Description("left")] int a, [Description("right")] int b) { counter.Value++; return a + b; }

        [ThalosTool]
        public static string Ping() => "pong";

        public static int NotATool() => 0;
    }

    [Fact]
    public async Task Discovers_annotated_methods_with_names_descriptions_and_schema()
    {
        var sp = new ServiceCollection().AddScoped<Counter>().BuildServiceProvider();
        var source = new LocalToolSource("local", sp, [typeof(MathTools)]);

        var tools = (await source.GetToolsAsync(default)).Value.Cast<AIFunction>().ToList();

        tools.Select(t => t.Name).Should().BeEquivalentTo(["add", "Ping"]);
        var add = tools.Single(t => string.Equals(t.Name, "add", StringComparison.Ordinal));
        add.Description.Should().Be("Adds two integers");
        add.JsonSchema.GetProperty("properties").TryGetProperty("a", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Each_invocation_gets_a_fresh_DI_scope()
    {
        var services = new ServiceCollection().AddScoped<Counter>().BuildServiceProvider();
        var source = new LocalToolSource("local", services, [typeof(MathTools)]);
        var add = (AIFunction)(await source.GetToolsAsync(default)).Value.Single(t => string.Equals(t.Name, "add", StringComparison.Ordinal));

        (await add.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["a"] = 2, ["b"] = 3 }))!.ToString().Should().Be("5");
        (await add.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["a"] = 1, ["b"] = 1 }))!.ToString().Should().Be("2");
        // Counter is scoped-per-invocation, so the root provider's counter (if resolved) is untouched — proves isolation
        services.GetRequiredService<Counter>().Value.Should().Be(0);
    }

    [Fact]
    public async Task Static_tools_invoke_without_an_instance()
    {
        var sp = new ServiceCollection().AddScoped<Counter>().BuildServiceProvider();
        var source = new LocalToolSource("local", sp, [typeof(MathTools)]);
        var ping = (AIFunction)(await source.GetToolsAsync(default)).Value.Single(t => string.Equals(t.Name, "Ping", StringComparison.Ordinal));

        (await ping.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)))!.ToString().Should().Be("pong");
    }

    [Fact]
    public async Task Rejects_types_that_are_not_marked_ThalosToolType()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var source = new LocalToolSource("local", sp, [typeof(Counter)]);

        var act = async () => await source.GetToolsAsync(default);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*ThalosToolType*");
    }
}
