using System.ComponentModel;
using System.Text.Json.Serialization;
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

        var result = await fn.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["suffix"] = "y", ["caller"] = new TestSecurityContext("forged") }, CancellationToken.None);

        result!.ToString().Should().Contain("user-9:y").And.NotContain("forged");
    }

    [Fact]
    public void Rejects_a_tool_parameter_typed_as_a_concrete_security_context_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(ForgedPrincipalTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*ForgeConcrete*parameter 'caller'*")
            .And.ParamName.Should().Be("toolTypes");
    }

    [Fact]
    public void Rejects_a_tool_parameter_typed_as_a_derived_security_interface_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(DerivedInterfaceTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*ForgeInterface*parameter 'caller'*")
            .And.ParamName.Should().Be("toolTypes");
    }

    /// <summary>A struct implementation, so <see cref="Nullable{T}"/> wrapping is reachable at the type level.</summary>
    public readonly struct PrincipalStruct : ISecurityContext
    {
        public string Id { get; init; }
        public IReadOnlySet<string> Roles => new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Claims => new Dictionary<string, string>(StringComparer.Ordinal);
    }

    [ThalosToolType]
    public sealed class NullableStructPrincipalTools(Counter counter)
    {
        [ThalosTool("nullable-struct")]
        public string Nullable(PrincipalStruct? caller, string suffix) { counter.Value++; return $"{caller?.Id}:{suffix}"; }
    }

    /// <summary>A second concrete <see cref="ISecurityContext"/>, used only nested inside a collection or a DTO
    /// property below — never as a parameter's own top-level type.</summary>
    public sealed class Principal : ISecurityContext
    {
        public string Id { get; set; } = string.Empty;
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    [ThalosToolType]
    public sealed class PrincipalListTools(Counter counter)
    {
        [ThalosTool("principal-list")]
        public string List(List<Principal> caller, string suffix) { counter.Value++; return $"{caller.Count}:{suffix}"; }
    }

    [ThalosToolType]
    public sealed class PrincipalArrayTools(Counter counter)
    {
        [ThalosTool("principal-array")]
        public string Array(ISecurityContext[] caller, string suffix) { counter.Value++; return $"{caller.Length}:{suffix}"; }
    }

    public sealed class DtoWithPrincipalProperty
    {
        public Principal Who { get; set; } = new();
    }

    [ThalosToolType]
    public sealed class DtoWithPrincipalPropertyTools(Counter counter)
    {
        [ThalosTool("dto-principal-property")]
        public string DtoPrincipal(DtoWithPrincipalProperty caller, string suffix) { counter.Value++; return $"{caller.Who.Id}:{suffix}"; }
    }

    public sealed class DtoWithSecurityContextProperty
    {
        public ISecurityContext Who { get; set; } = AnonymousSecurityContext.Instance;
    }

    [ThalosToolType]
    public sealed class DtoWithSecurityContextPropertyTools(Counter counter)
    {
        [ThalosTool("dto-isecuritycontext-property")]
        public string DtoContext(DtoWithSecurityContextProperty caller, string suffix) { counter.Value++; return $"{caller.Who.Id}:{suffix}"; }
    }

    public sealed class NestedMiddleDto
    {
        public Principal Deep { get; set; } = new();
    }

    public sealed class NestedOuterDto
    {
        public NestedMiddleDto Middle { get; set; } = new();
    }

    [ThalosToolType]
    public sealed class NestedDtoTools(Counter counter)
    {
        [ThalosTool("nested-dto")]
        public string Nested(NestedOuterDto caller, string suffix) { counter.Value++; return $"{caller.Middle.Deep.Id}:{suffix}"; }
    }

    [ThalosToolType]
    public sealed class ByRefPrincipalTools(Counter counter)
    {
        [ThalosTool("byref-principal")]
        public string ByRef(in ISecurityContext caller, string suffix) { counter.Value++; return $"{caller.Id}:{suffix}"; }
    }

    public sealed class OrdinaryDto
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    [ThalosToolType]
    public sealed class OrdinaryDtoTools(Counter counter)
    {
        [ThalosTool("ordinary-dto")]
        public string Ordinary(OrdinaryDto request, string suffix) { counter.Value++; return $"{request.Name}:{suffix}"; }
    }

    [Fact]
    public void Rejects_a_nullable_struct_security_context_parameter_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(NullableStructPrincipalTools)]);

        act.Should().Throw<ArgumentException>()
            // the top-level Nullable unwrap happens before the shape walk even starts, so the path is the bare
            // parameter name, never a collection-style "[]" suffix
            .WithMessage("*Nullable*parameter 'caller'*at 'caller'.*")
            .And.ParamName.Should().Be("toolTypes");
    }

    [Fact]
    public void Rejects_a_list_of_security_contexts_parameter_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(PrincipalListTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*List*parameter 'caller'*at 'caller[]'.*")
            .And.ParamName.Should().Be("toolTypes");
    }

    [Fact]
    public void Rejects_an_array_of_security_contexts_parameter_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(PrincipalArrayTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Array*parameter 'caller'*at 'caller[]'.*")
            .And.ParamName.Should().Be("toolTypes");
    }

    [Fact]
    public void Rejects_a_dto_with_a_principal_property_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(DtoWithPrincipalPropertyTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*DtoPrincipal*parameter 'caller'*'caller.who'*")
            .And.ParamName.Should().Be("toolTypes");
    }

    [Fact]
    public void Rejects_a_dto_with_an_ISecurityContext_property_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(DtoWithSecurityContextPropertyTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*DtoContext*parameter 'caller'*'caller.who'*")
            .And.ParamName.Should().Be("toolTypes");
    }

    [Fact]
    public void Rejects_a_principal_nested_two_levels_deep_in_a_dto_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(NestedDtoTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Nested*parameter 'caller'*'caller.middle.deep'*")
            .And.ParamName.Should().Be("toolTypes");
    }

    [Fact]
    public void Rejects_a_by_ref_security_context_parameter_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(ByRefPrincipalTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*ByRef*parameter 'caller'*at 'caller'.*")
            .And.ParamName.Should().Be("toolTypes");
    }

    [Fact]
    public async Task An_ordinary_dto_with_no_principal_anywhere_still_registers()
    {
        var fn = await SingleToolAsync(typeof(OrdinaryDtoTools));

        fn.JsonSchema.GetRawText().Should().Contain("request").And.Contain("suffix");
    }

    // --- Bypass shapes the hand-written reflection walk missed: System.Text.Json's own type info is walked
    // instead, so these are caught the same way the model's deserializer would actually reach them. ---

    /// <summary>A generic DTO. <c>JsonTypeInfo.Properties</c> for a closed generic type reports its own declared
    /// properties directly — unlike the retired reflection walk, which only ever inspected a generic type's type
    /// arguments and never its properties.</summary>
    public sealed class GenericPrincipalDto<T>
    {
        public Principal Who { get; set; } = new();
        public T Payload { get; set; } = default!;
    }

    [ThalosToolType]
    public sealed class GenericDtoTools(Counter counter)
    {
        [ThalosTool("generic-dto")]
        public string Generic(GenericPrincipalDto<int> caller, string suffix) { counter.Value++; return $"{caller.Who.Id}:{suffix}"; }
    }

    [Fact]
    public void Rejects_a_generic_dto_with_a_principal_property_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(GenericDtoTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Generic*parameter 'caller'*'caller.who'*")
            .And.ParamName.Should().Be("toolTypes");
    }

    /// <summary>A class that subclasses <see cref="List{T}"/> rather than being generic itself.
    /// <c>JsonTypeInfo.ElementType</c> reports <see cref="Principal"/> for it because System.Text.Json recognizes
    /// it as an <see cref="IEnumerable{T}"/> of <see cref="Principal"/> — the retired reflection walk's
    /// <c>IsGenericType</c> branch never fired for this type (it is not itself generic) and fell through to
    /// walking its own properties, which are just <c>Count</c>/<c>Capacity</c>.</summary>
    public sealed class PrincipalListSubclass : List<Principal>;

    [ThalosToolType]
    public sealed class PrincipalListSubclassTools(Counter counter)
    {
        [ThalosTool("principal-list-subclass")]
        public string ListSubclass(PrincipalListSubclass caller, string suffix) { counter.Value++; return $"{caller.Count}:{suffix}"; }
    }

    [Fact]
    public void Rejects_a_class_that_subclasses_a_list_of_security_contexts_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(PrincipalListSubclassTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*ListSubclass*parameter 'caller'*at 'caller[]'.*")
            .And.ParamName.Should().Be("toolTypes");
    }

    public sealed class FieldPrincipalDto
    {
        [JsonInclude]
        internal Principal Who = new();
    }

    [ThalosToolType]
    public sealed class FieldPrincipalDtoTools(Counter counter)
    {
        [ThalosTool("field-principal-dto")]
        public string FieldDto(FieldPrincipalDto caller, string suffix) { counter.Value++; return $"{caller.Who.Id}:{suffix}"; }
    }

    [Fact]
    public void Rejects_a_json_include_field_that_is_a_principal_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(FieldPrincipalDtoTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*FieldDto*parameter 'caller'*'caller.who'*")
            .And.ParamName.Should().Be("toolTypes");
    }

    public sealed class InternalPropertyPrincipalDto
    {
        [JsonInclude]
        internal Principal Who { get; set; } = new();
    }

    [ThalosToolType]
    public sealed class InternalPropertyPrincipalDtoTools(Counter counter)
    {
        [ThalosTool("internal-property-principal-dto")]
        public string InternalPropertyDto(InternalPropertyPrincipalDto caller, string suffix) { counter.Value++; return $"{caller.Who.Id}:{suffix}"; }
    }

    [Fact]
    public void Rejects_a_json_include_internal_property_that_is_a_principal_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(InternalPropertyPrincipalDtoTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*InternalPropertyDto*parameter 'caller'*'caller.who'*")
            .And.ParamName.Should().Be("toolTypes");
    }

    [JsonPolymorphic]
    [JsonDerivedType(typeof(PolymorphicPrincipal), "principal")]
    public class PolymorphicBase;

    public sealed class PolymorphicPrincipal : PolymorphicBase, ISecurityContext
    {
        public string Id { get; set; } = string.Empty;
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    [ThalosToolType]
    public sealed class PolymorphicPrincipalTools(Counter counter)
    {
        [ThalosTool("polymorphic-principal")]
        public string Polymorphic(PolymorphicBase caller, string suffix) { counter.Value++; return $"{caller.GetType().Name}:{suffix}"; }
    }

    [Fact]
    public void Rejects_a_json_derived_type_that_is_a_principal_eagerly()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => new LocalToolSource("local", sp, [typeof(PolymorphicPrincipalTools)]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Polymorphic*parameter 'caller'*'caller:PolymorphicPrincipal'*")
            .And.ParamName.Should().Be("toolTypes");
    }

    // --- False positives the review checked for: none of these shapes carry a principal anywhere, so
    // registration must succeed. ---

    public interface IUnrelatedInterface
    {
        string Name { get; }
    }

    public sealed class FalsePositiveCheckDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Uri? Website { get; set; }
        public IReadOnlyList<string> Tags { get; set; } = [];
        public Dictionary<string, object> Metadata { get; set; } = [];
        public IUnrelatedInterface? Other { get; set; }
    }

    [ThalosToolType]
    public sealed class FalsePositiveCheckTools(Counter counter)
    {
        [ThalosTool("false-positive-check")]
        public string Check(FalsePositiveCheckDto request, string suffix) { counter.Value++; return $"{request.Name}:{suffix}"; }
    }

    [Fact]
    public async Task A_dto_with_common_bcl_shapes_and_an_unrelated_interface_property_does_not_false_positive()
    {
        var fn = await SingleToolAsync(typeof(FalsePositiveCheckTools));

        fn.JsonSchema.GetRawText().Should().Contain("request").And.Contain("suffix");
    }

    // --- Performance: the walk must be linear, memoised per Type, not exponential in a DTO graph that shares
    // types. Thirteen levels of three properties each, all reusing the previous level's type. ---

    public sealed class PerfLevel0
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public string C { get; set; } = "";
    }

    public sealed class PerfLevel1
    {
        public PerfLevel0 A { get; set; } = new();
        public PerfLevel0 B { get; set; } = new();
        public PerfLevel0 C { get; set; } = new();
    }

    public sealed class PerfLevel2
    {
        public PerfLevel1 A { get; set; } = new();
        public PerfLevel1 B { get; set; } = new();
        public PerfLevel1 C { get; set; } = new();
    }

    public sealed class PerfLevel3
    {
        public PerfLevel2 A { get; set; } = new();
        public PerfLevel2 B { get; set; } = new();
        public PerfLevel2 C { get; set; } = new();
    }

    public sealed class PerfLevel4
    {
        public PerfLevel3 A { get; set; } = new();
        public PerfLevel3 B { get; set; } = new();
        public PerfLevel3 C { get; set; } = new();
    }

    public sealed class PerfLevel5
    {
        public PerfLevel4 A { get; set; } = new();
        public PerfLevel4 B { get; set; } = new();
        public PerfLevel4 C { get; set; } = new();
    }

    public sealed class PerfLevel6
    {
        public PerfLevel5 A { get; set; } = new();
        public PerfLevel5 B { get; set; } = new();
        public PerfLevel5 C { get; set; } = new();
    }

    public sealed class PerfLevel7
    {
        public PerfLevel6 A { get; set; } = new();
        public PerfLevel6 B { get; set; } = new();
        public PerfLevel6 C { get; set; } = new();
    }

    public sealed class PerfLevel8
    {
        public PerfLevel7 A { get; set; } = new();
        public PerfLevel7 B { get; set; } = new();
        public PerfLevel7 C { get; set; } = new();
    }

    public sealed class PerfLevel9
    {
        public PerfLevel8 A { get; set; } = new();
        public PerfLevel8 B { get; set; } = new();
        public PerfLevel8 C { get; set; } = new();
    }

    public sealed class PerfLevel10
    {
        public PerfLevel9 A { get; set; } = new();
        public PerfLevel9 B { get; set; } = new();
        public PerfLevel9 C { get; set; } = new();
    }

    public sealed class PerfLevel11
    {
        public PerfLevel10 A { get; set; } = new();
        public PerfLevel10 B { get; set; } = new();
        public PerfLevel10 C { get; set; } = new();
    }

    public sealed class PerfLevel12
    {
        public PerfLevel11 A { get; set; } = new();
        public PerfLevel11 B { get; set; } = new();
        public PerfLevel11 C { get; set; } = new();
    }

    public sealed class PerfLevel13
    {
        public PerfLevel12 A { get; set; } = new();
        public PerfLevel12 B { get; set; } = new();
        public PerfLevel12 C { get; set; } = new();
    }

    [ThalosToolType]
    public sealed class PerfTools(Counter counter)
    {
        [ThalosTool("perf")]
        public string Perf(PerfLevel13 request, string suffix) { counter.Value++; return $"{request is null}:{suffix}"; }
    }

    [Fact]
    public void A_deep_shared_type_dto_is_walked_once_per_distinct_type_because_the_walk_is_memoised()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var perf = typeof(PerfTools).GetMethod(nameof(PerfTools.Perf))!;
        var reachable = ReachableTypes(perf);
        var ownDtoCount = reachable.Count(t => t.Assembly == typeof(LocalToolSourceTests).Assembly);

        var source = new LocalToolSource("local", sp, [typeof(PerfTools)]);

        // Red when the memo is removed: the unmemoised walk looks up each path through the shared-type graph,
        // millions of lookups rather than one per distinct type.
        source.ShapeTypeInfoLookups.Should().BeLessThanOrEqualTo(reachable.Count);
        // Red when the lookup counter is not incremented: every PerfLevel type has to be looked up at least once, so
        // a dead counter cannot pass the bound above by reporting zero.
        source.ShapeTypeInfoLookups.Should().BeGreaterThanOrEqualTo(ownDtoCount);
    }

    /// <summary>
    /// The distinct types a tool method's parameters reach through public properties, descending only into this
    /// test assembly's own DTOs and treating every other type, such as <see cref="string"/>, as a leaf.
    /// </summary>
    private static HashSet<Type> ReachableTypes(System.Reflection.MethodInfo method)
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>(method.GetParameters().Select(p => p.ParameterType));
        while (pending.TryPop(out var type))
        {
            if (seen.Add(type) && type.Assembly == typeof(LocalToolSourceTests).Assembly)
            {
                foreach (var property in type.GetProperties())
                {
                    pending.Push(property.PropertyType);
                }
            }
        }

        return seen;
    }
}
