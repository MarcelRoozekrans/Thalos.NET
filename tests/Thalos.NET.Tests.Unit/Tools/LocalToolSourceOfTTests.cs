using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Runtime;
using Thalos.Tests.Unit.Runtime;
using Thalos.Tools;
using ZeroAlloc.Authorization;
using static Thalos.Tests.Unit.Tools.LocalToolSourceTests;

namespace Thalos.Tests.Unit.Tools;

/// <summary>
/// The reflection-free <see cref="LocalToolSource{TTools}"/> and <see cref="ThalosBuilder.AddLocalTools{TTools}"/>, with
/// source-generated JSON metadata only, so no assertion here can pass on reflection-based JSON.
/// </summary>
public sealed class LocalToolSourceOfTTests
{
    private static readonly JsonSerializerOptions Json = new(AIJsonUtilities.DefaultOptions) { TypeInfoResolver = LocalToolsJsonContext.Default };

    /// <summary>Counts the disposed tool instances of one test; a singleton, so no static is shared between tests.</summary>
    public sealed class DisposeLog
    {
        public int Count { get; set; }
    }

    [ThalosToolType]
    public sealed class ScopedTools(Counter counter, DisposeLog log) : IDisposable
    {
        [ThalosTool("count")]
        [Description("Counts the calls this instance's scope has seen.")]
        public int Count() => ++counter.Value;

        [ThalosTool("delay")]
        public async Task<string> DelayAsync(string text, CancellationToken ct)
        {
            await Task.Delay(1, ct);
            return $"done:{text}:{counter.Value}";
        }

        [ThalosTool("scoped")]
        public string ScopeMatches(AIFunctionArguments arguments) => ReferenceEquals(arguments.Services?.GetService<Counter>(), counter) ? "same-scope" : "other";

        [ThalosTool]
        public static string Ping() => "pong";

        public static int NotATool() => 0;

        public void Dispose() => log.Count++;
    }

    [ThalosToolType]
    public sealed class EchoTools
    {
        [ThalosTool("echo")]
        public static string Echo(string text) => text;

        [ThalosTool]
        public static string Ping() => "pong from echo";
    }

    private static ServiceProvider Services() => new ServiceCollection().AddScoped<Counter>().AddSingleton<DisposeLog>().BuildServiceProvider();

    private static async Task<List<AIFunction>> ToolsAsync<TTools>(IServiceProvider services, params Func<TTools, Delegate>[] binders)
        where TTools : class
    {
        var source = new LocalToolSource<TTools>("local", services, Json, binders);
        return [.. (await source.GetToolsAsync(default)).Value.Cast<AIFunction>()];
    }

    [Fact]
    public async Task Names_and_descriptions_come_from_the_bound_methods()
    {
        var tools = await ToolsAsync<ScopedTools>(Services(), t => t.Count, _ => ScopedTools.Ping);

        // Red: name each tool by method.Name instead of its [ThalosTool] name: count becomes Count.
        tools.Select(t => t.Name).Should().Equal("count", "Ping");
        // Red: pass the tool's name as the description: the [Description] text is replaced.
        tools[0].Description.Should().Be("Counts the calls this instance's scope has seen.");
    }

    [Fact]
    public async Task The_caller_is_bound_to_the_turn_and_never_read_from_the_model()
    {
        var whoami = (await ToolsAsync<CallerEchoTools>(Services(), t => t.WhoAmI)).Single();
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new TestSecurityContext("user-7"));

        var result = await whoami.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["suffix"] = "x", ["caller"] = new TestSecurityContext("forged") });

        // Both assertions report, so the one change below shows each of them red.
        using var assertions = new AssertionScope();
        // Red: give AIFunctionFactory plain options without LocalToolSourceBinding.Options' parameter binding: the
        // forged caller from the arguments is used.
        result!.ToString().Should().Be("user-7:x");
        // Red: same change: the caller parameter appears in the schema.
        whoami.JsonSchema.GetProperty("properties").TryGetProperty("caller", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Each_call_runs_on_a_fresh_instance_in_its_own_scope_which_is_disposed()
    {
        var services = Services();
        var tools = await ToolsAsync<ScopedTools>(services, t => t.Count, t => t.ScopeMatches);
        var log = services.GetRequiredService<DisposeLog>();
        var disposedBefore = log.Count;

        var first = await tools[0].InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal));
        var second = await tools[0].InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal));

        // Red: add the probe function for an instance method instead of the scoped tool: both calls share the probe's
        // counter, and the second reads 2.
        new[] { first!.ToString(), second!.ToString() }.Should().Equal("1", "1");
        // Red: drop the instance's disposal from the scoped tool's finally block.
        log.Count.Should().Be(disposedBefore + 2);
        // Red: leave Services off the scoped arguments: the tool sees no scope and answers "other".
        (await tools[1].InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)))!.ToString().Should().Be("same-scope");
    }

    [Fact]
    public async Task Parameters_the_factory_binds_itself_need_no_JSON_metadata()
    {
        var services = Services();

        var create = () => ToolsAsync<ScopedTools>(services, t => t.DelayAsync, t => t.ScopeMatches);

        // Red: drop IsBoundByFactory from the shape guard: the source-generated options have no metadata for
        // CancellationToken or AIFunctionArguments, and GetToolsAsync throws NotSupportedException.
        var tools = (await create.Should().NotThrowAsync()).Subject;
        (await tools[0].InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["text"] = "x" }))!.ToString().Should().Be("done:x:0");
    }

    [Fact]
    public async Task A_binder_must_return_one_of_the_types_own_tool_methods_once()
    {
        var services = Services();

        var notATool = () => ToolsAsync<ScopedTools>(services, _ => ScopedTools.NotATool);
        // Red: drop the [ThalosTool] check from ToolMethod: NotATool becomes a tool.
        await notATool.Should().ThrowAsync<InvalidOperationException>().WithMessage("*must return one of its [ThalosTool] methods*NotATool'.");

        var foreign = () => ToolsAsync<ScopedTools>(services, _ => StaticCallerEchoTools.WhoAmIStatic);
        // Red: drop the DeclaringType check from ToolMethod: another type's tool method is accepted.
        await foreign.Should().ThrowAsync<InvalidOperationException>().WithMessage("*it returned '*StaticCallerEchoTools.WhoAmIStatic'.");

        var twice = () => ToolsAsync<ScopedTools>(services, t => t.Count, t => t.Count);
        // Red: drop the seen-set check: count is registered twice.
        await twice.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Count' is named by more than one binder.");
    }

    [Fact]
    public async Task A_principal_hidden_in_a_parameter_shape_is_refused()
    {
        var act = () => ToolsAsync<ForgedPrincipalTools>(Services(), t => t.ForgeConcrete);

        // Red: skip the shape guard in LocalToolSource<TTools>.Create: the forged principal becomes a model argument.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*parameter 'caller' has a shape that contains a type assignable to {nameof(ISecurityContext)}*");
    }

    [Fact]
    public async Task AddLocalTools_of_T_registers_the_source_and_refuses_an_unmarked_type_or_no_binder()
    {
        var services = new ServiceCollection().AddScoped<Counter>().AddSingleton<DisposeLog>();
        services.AddThalos(t => t.AddLocalTools<ScopedTools>("local", Json, s => s.Count));
        await using var provider = services.BuildServiceProvider();

        // Red: register nothing in AddLocalTools<TTools>: no tool source named local resolves.
        var source = provider.GetServices<IToolSource>().Single(s => string.Equals(s.Name, "local", StringComparison.Ordinal));
        (await source.GetToolsAsync(default)).Value.Select(t => t.Name).Should().Equal("count");

        var unmarked = () => new ServiceCollection().AddThalos(t => t.AddLocalTools<Counter>("local", Json, c => c.ToString));
        // Red: drop the [ThalosToolType] check from AddLocalTools<TTools>.
        unmarked.Should().Throw<ArgumentException>().WithMessage("*is not marked [ThalosToolType]*");

        var none = () => new ServiceCollection().AddThalos(t => t.AddLocalTools<ScopedTools>("local", Json));
        // Red: drop the empty-binder check from AddLocalTools<TTools>.
        none.Should().Throw<ArgumentException>().WithMessage("At least one tool binder is required.*");
    }

    [Fact]
    public void AddLocalTools_of_T_refuses_an_invalid_source_name_at_registration()
    {
        var invalid = () => new ServiceCollection().AddThalos(t => t.AddLocalTools<ScopedTools>("lo__cal", Json, s => s.Count));

        // Red: drop the ToolSourceName check from AddLocalTools<TTools>: the name is accepted here and only refused
        // when the source is first resolved.
        invalid.Should().Throw<ArgumentException>().WithMessage("Tool source name 'lo__cal' is invalid*").WithParameterName("sourceName");
    }

    [Fact]
    public void LocalToolSources_Create_refuses_null_services_an_unmarked_type_or_no_binder()
    {
        using var scope = new AssertionScope();
        var services = new ServiceCollection().BuildServiceProvider();

        var noServices = () => LocalToolSources.Create<ScopedTools>("local", null!, Json, s => s.Count);
        // Red: drop the services null check from LocalToolSource<TTools>'s constructor, which Create relies on.
        noServices.Should().Throw<ArgumentNullException>("no container is refused").WithParameterName("services");

        var unmarked = () => LocalToolSources.Create<Counter>("local", services, Json, c => c.ToString);
        // Red: drop the [ThalosToolType] check from LocalToolSources.ThrowIfInvalid: the constructor still refuses the
        // type, but against its binders parameter, not tools.
        unmarked.Should().Throw<ArgumentException>("an unmarked type is refused").WithMessage("*is not marked [ThalosToolType]*").WithParameterName("tools");

        var none = () => LocalToolSources.Create<ScopedTools>("local", services, Json);
        // Red: drop the empty-binder check from LocalToolSources.ThrowIfInvalid.
        none.Should().Throw<ArgumentException>("an empty binder list is refused").WithMessage("At least one tool binder is required.*").WithParameterName("tools");
    }

    /// <summary>
    /// Several typed classes share one source name through the builder overload: one source, every class's tools, in
    /// the order the classes were added. Red: register one source per class instead of the combined one; two sources
    /// named local resolve.
    /// </summary>
    [Fact]
    public async Task AddLocalTools_with_a_builder_serves_several_classes_as_one_source()
    {
        var services = new ServiceCollection().AddScoped<Counter>().AddSingleton<DisposeLog>();
        services.AddThalos(t => t.AddLocalTools("local", Json, tools => tools
            .Add<ScopedTools>(s => s.Count)
            .Add<EchoTools>(_ => EchoTools.Echo)));
        await using var provider = services.BuildServiceProvider();

        var source = provider.GetServices<IToolSource>().Should().ContainSingle(s => string.Equals(s.Name, "local", StringComparison.Ordinal)).Subject;
        var tools = (await source.GetToolsAsync(default)).Value.Cast<AIFunction>().ToList();
        tools.Select(t => t.Name).Should().Equal("count", "echo");
        (await tools[0].InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { Services = provider }))!.ToString().Should().Be("1");
        (await tools[1].InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["text"] = "hi", Services = provider }))!.ToString().Should().Be("hi");
    }

    /// <summary>Red: drop the ToolSourceNames.Add call from the builder overload; each clash below is then accepted.</summary>
    [Fact]
    public void A_builder_source_still_refuses_a_name_another_source_has_in_either_order()
    {
        using var scope = new AssertionScope();
        var builderFirst = () => new ServiceCollection().AddThalos(t => t
            .AddLocalTools("local", Json, tools => tools.Add<EchoTools>(_ => EchoTools.Echo))
            .AddLocalTools<ScopedTools>("local", Json, s => s.Count));
        var typedFirst = () => new ServiceCollection().AddThalos(t => t
            .AddLocalTools<ScopedTools>("local", Json, s => s.Count)
            .AddLocalTools("local", Json, tools => tools.Add<EchoTools>(_ => EchoTools.Echo)));
        var twoBuilders = () => new ServiceCollection().AddThalos(t => t
            .AddLocalTools("local", Json, tools => tools.Add<EchoTools>(_ => EchoTools.Echo))
            .AddLocalTools("local", Json, tools => tools.Add<ScopedTools>(s => s.Count)));

        builderFirst.Should().Throw<ArgumentException>().WithMessage("A local tool source named 'local' was already added*");
        typedFirst.Should().Throw<ArgumentException>().WithMessage("A local tool source named 'local' was already added*");
        twoBuilders.Should().Throw<ArgumentException>().WithMessage("A local tool source named 'local' was already added*");
    }

    [Fact]
    public void A_builder_refuses_no_class_the_same_class_twice_an_unmarked_class_or_no_binder()
    {
        using var scope = new AssertionScope();
        var empty = () => new ServiceCollection().AddThalos(t => t.AddLocalTools("local", Json, _ => { }));
        var twice = () => new ServiceCollection().AddThalos(t => t.AddLocalTools("local", Json, tools => tools
            .Add<EchoTools>(_ => EchoTools.Echo)
            .Add<EchoTools>(_ => EchoTools.Ping)));
        var unmarked = () => new ServiceCollection().AddThalos(t => t.AddLocalTools("local", Json, tools => tools.Add<Counter>(c => c.ToString)));
        var none = () => new ServiceCollection().AddThalos(t => t.AddLocalTools("local", Json, tools => tools.Add<EchoTools>()));
        var invalidName = () => new ServiceCollection().AddThalos(t => t.AddLocalTools("lo__cal", Json, tools => tools.Add<EchoTools>(_ => EchoTools.Echo)));

        empty.Should().Throw<ArgumentException>().WithMessage("At least one tool class is required.*").WithParameterName("configure");
        twice.Should().Throw<ArgumentException>().WithMessage("*EchoTools' was already added to tool source 'local'*");
        unmarked.Should().Throw<ArgumentException>().WithMessage("*is not marked [ThalosToolType]*");
        none.Should().Throw<ArgumentException>().WithMessage("At least one tool binder is required.*");
        invalidName.Should().Throw<ArgumentException>().WithParameterName("sourceName");
    }

    /// <summary>Red: drop the name check in the combined source; the catalog would then keep the first ping silently.</summary>
    [Fact]
    public async Task Two_classes_of_one_builder_source_that_share_a_tool_name_fail_its_first_resolution()
    {
        var services = new ServiceCollection().AddScoped<Counter>().AddSingleton<DisposeLog>();
        services.AddThalos(t => t.AddLocalTools("local", Json, tools => tools
            .Add<ScopedTools>(_ => ScopedTools.Ping)
            .Add<EchoTools>(_ => EchoTools.Ping)));
        await using var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IToolSource>().Single(s => string.Equals(s.Name, "local", StringComparison.Ordinal));

        var act = async () => await source.GetToolsAsync(default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Tool 'Ping' is defined by more than one class of tool source 'local'.");
    }

    [Fact]
    public void LocalToolSources_CreateOptions_are_read_only_with_the_default_encoder_and_the_given_resolver()
    {
        var options = LocalToolSources.CreateOptions(LocalToolsJsonContext.Default);

        using var scope = new AssertionScope();

        // Red: remove MakeReadOnly from CreateOptions.
        options.IsReadOnly.Should().BeTrue();

        // Red: copy new JsonSerializerOptions instead of AIJsonUtilities.DefaultOptions: the encoder is the default one.
        options.Encoder.Should().BeSameAs(AIJsonUtilities.DefaultOptions.Encoder);

        // Red: keep AIJsonUtilities.DefaultOptions' resolver: the given one is not used.
        options.TypeInfoResolver.Should().BeSameAs(LocalToolsJsonContext.Default);
    }
}

/// <summary>
/// The tools' JSON metadata. It holds <see cref="ISecurityContext"/> and the forged principal too, so that leaving the
/// caller out of the schema and refusing the forged one are the binding's and the guard's doing, not a gap in the metadata.
/// </summary>
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ISecurityContext))]
[JsonSerializable(typeof(ForgedConcretePrincipal))]
internal sealed partial class LocalToolsJsonContext : JsonSerializerContext;
