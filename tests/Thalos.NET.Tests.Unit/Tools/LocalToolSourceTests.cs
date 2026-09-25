using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Runtime;
using Thalos.Tests.Unit.Runtime;
using Thalos.Tools;
using ZeroAlloc.Authorization;

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

        [ThalosTool("count")]
        public int Count() => ++counter.Value;

        [ThalosTool]
        public static string Ping() => "pong";

        public static int NotATool() => 0;
    }

    [ThalosToolType]
    public sealed class AsyncTools(Counter counter) : IDisposable
    {
        public static int Disposed { get; set; }

        [ThalosTool("delay")]
        public async Task<string> DelayAsync(string text, CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            return "done:" + text + (counter is null ? "!" : ""); // touch instance state (CA1822)
        }

        /// <summary>True when <see cref="AIFunctionArguments.Services"/> is the very scope this instance was created from.</summary>
        [ThalosTool("scoped")]
        public string ScopeMatches(AIFunctionArguments arguments) => ReferenceEquals(arguments.Services?.GetService<Counter>(), counter) ? "same-scope" : "other";

        public void Dispose() => Disposed++;
    }

    [ThalosToolType]
    public sealed class CallerEchoTools(Counter counter)
    {
        [ThalosTool("whoami")]
        public string WhoAmI(ISecurityContext caller, string suffix) { counter.Value++; return $"{caller.Id}:{suffix}"; }
    }

    [ThalosToolType]
    public sealed class StaticCallerEchoTools
    {
        [ThalosTool("whoami-static")]
        public static string WhoAmIStatic(ISecurityContext caller, string suffix) => $"{caller.Id}:{suffix}";
    }

    /// <summary>A concrete <see cref="ISecurityContext"/> implementation with a settable <see cref="Id"/>, standing
    /// in for Daedalus's real principal types (e.g. a detached principal or a claims-backed context) that a tool
    /// author could mistakenly declare directly instead of the abstraction <see cref="LocalToolSource"/> binds.
    /// Declared as a parameter type it would appear in the JSON schema and accept a model-supplied <c>id</c>.</summary>
    public sealed class ForgedConcretePrincipal : ISecurityContext
    {
        public string Id { get; set; } = string.Empty;
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    [ThalosToolType]
    public sealed class ForgedPrincipalTools(Counter counter)
    {
        [ThalosTool("forge-concrete")]
        public string ForgeConcrete(ForgedConcretePrincipal caller, string suffix) { counter.Value++; return $"{caller.Id}:{suffix}"; }
    }

    public interface IExtendedSecurityContext : ISecurityContext
    {
        string ExtraClaim { get; }
    }

    [ThalosToolType]
    public sealed class DerivedInterfaceTools(Counter counter)
    {
        [ThalosTool("forge-interface")]
        public string ForgeInterface(IExtendedSecurityContext caller, string suffix) { counter.Value++; return $"{caller.Id}:{suffix}"; }
    }

    private static async Task<AIFunction> ToolAsync(IServiceProvider sp, string name, params Type[] types)
    {
        var source = new LocalToolSource("local", sp, types);
        return (AIFunction)(await source.GetToolsAsync(default)).Value.Single(t => string.Equals(t.Name, name, StringComparison.Ordinal));
    }

    private static async Task<AIFunction> SingleToolAsync(Type toolType)
    {
        var sp = new ServiceCollection().AddScoped<Counter>().BuildServiceProvider();
        var source = new LocalToolSource("local", sp, [toolType]);
        return (AIFunction)(await source.GetToolsAsync(default)).Value.Single();
    }

    [Fact]
    public async Task Discovers_annotated_methods_with_names_descriptions_and_schema()
    {
        var sp = new ServiceCollection().AddScoped<Counter>().BuildServiceProvider();
        var source = new LocalToolSource("local", sp, [typeof(MathTools)]);

        var tools = (await source.GetToolsAsync(default)).Value.Cast<AIFunction>().ToList();

        tools.Select(t => t.Name).Should().BeEquivalentTo(["add", "count", "Ping"]);
        var add = tools.Single(t => string.Equals(t.Name, "add", StringComparison.Ordinal));
        add.Description.Should().Be("Adds two integers");
        add.JsonSchema.GetProperty("properties").TryGetProperty("a", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Each_invocation_gets_a_fresh_DI_scope()
    {
        var services = new ServiceCollection().AddScoped<Counter>().BuildServiceProvider();
        var add = await ToolAsync(services, "add", typeof(MathTools));

        (await add.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["a"] = 2, ["b"] = 3 }))!.ToString().Should().Be("5");
        (await add.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["a"] = 1, ["b"] = 1 }))!.ToString().Should().Be("2");
        // Counter is scoped-per-invocation, so the root provider's counter (if resolved) is untouched — proves isolation
        services.GetRequiredService<Counter>().Value.Should().Be(0);
    }

    [Fact]
    public async Task Two_invocations_observe_different_scoped_instances()
    {
        var services = new ServiceCollection().AddScoped<Counter>().BuildServiceProvider();
        var count = await ToolAsync(services, "count", typeof(MathTools));

        // a fresh scoped Counter per call → each call increments from 0 and reads 1
        (await count.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)))!.ToString().Should().Be("1");
        (await count.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)))!.ToString().Should().Be("1");
    }

    [Fact]
    public async Task Async_tool_with_cancellation_token_works_and_the_instance_is_disposed()
    {
        var services = new ServiceCollection().AddScoped<Counter>().BuildServiceProvider();
        var delay = await ToolAsync(services, "delay", typeof(AsyncTools));
        var before = AsyncTools.Disposed;

        var result = await delay.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["text"] = "x" }, CancellationToken.None);

        result!.ToString().Should().Be("done:x");
        AsyncTools.Disposed.Should().Be(before + 1);
    }

    [Fact]
    public async Task Per_call_scope_is_exposed_through_AIFunctionArguments_Services()
    {
        var services = new ServiceCollection().AddScoped<Counter>().BuildServiceProvider();
        var scoped = await ToolAsync(services, "scoped", typeof(AsyncTools));

        // the caller passed no Services; the tool still sees the per-call scope its own instance came from
        (await scoped.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)))!.ToString().Should().Be("same-scope");
    }

    [Fact]
    public async Task Static_tools_invoke_without_an_instance()
    {
        var sp = new ServiceCollection().AddScoped<Counter>().BuildServiceProvider();
        var ping = await ToolAsync(sp, "Ping", typeof(MathTools));

        (await ping.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)))!.ToString().Should().Be("pong");
    }

    [Fact]
    public void Rejects_types_that_are_not_marked_ThalosToolType_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(Counter)]);

        act.Should().Throw<ArgumentException>().WithMessage("*ThalosToolType*").And.ParamName.Should().Be("toolTypes");
    }

    [Fact]
    public void Constructor_guards_its_arguments()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var withNullName = () => new LocalToolSource(null!, sp, []);
        var withNullServices = () => new LocalToolSource("local", null!, []);
        var withNullTypes = () => new LocalToolSource("local", sp, null!);

        withNullName.Should().Throw<ArgumentException>();
        withNullServices.Should().Throw<ArgumentNullException>();
        withNullTypes.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task A_security_context_parameter_is_not_in_the_schema()
    {
        var fn = await SingleToolAsync(typeof(CallerEchoTools));

        fn.JsonSchema.GetRawText().Should().NotContain("caller").And.Contain("suffix");
    }

    [Fact]
    public async Task The_turn_caller_is_bound_not_a_model_argument()
    {
        var fn = await SingleToolAsync(typeof(CallerEchoTools));
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestSecurityContext("user-7"));

        var result = await fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["suffix"] = "x", ["caller"] = new TestSecurityContext("forged") }, CancellationToken.None);

        result!.ToString().Should().Contain("user-7:x").And.NotContain("forged");
    }

    [Fact]
    public async Task Outside_a_turn_the_caller_is_anonymous()
    {
        var fn = await SingleToolAsync(typeof(CallerEchoTools));

        (await fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["suffix"] = "x" }, CancellationToken.None))!.ToString()
            .Should().Contain(AnonymousSecurityContext.AnonymousId);
    }

    [Fact]
    public async Task A_static_tools_security_context_parameter_is_also_bound_from_the_turn()
    {
        var fn = await SingleToolAsync(typeof(StaticCallerEchoTools));
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestSecurityContext("user-9"));

        var result = await fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["suffix"] = "y" }, CancellationToken.None);

        result!.ToString().Should().Contain("user-9:y");
    }

    [Fact]
    public void Rejects_a_tool_parameter_typed_as_a_concrete_security_context_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(ForgedPrincipalTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*ForgeConcrete*caller*")
            .And.ParamName.Should().Be("toolTypes");
    }

    [Fact]
    public void Rejects_a_tool_parameter_typed_as_a_derived_security_interface_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(DerivedInterfaceTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*ForgeInterface*caller*")
            .And.ParamName.Should().Be("toolTypes");
    }
}
