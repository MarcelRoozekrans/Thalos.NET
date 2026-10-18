using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tools;

/// <summary>
/// The reflection-free form of <see cref="LocalToolSource"/>: the tools of one <see cref="ThalosToolTypeAttribute"/>
/// class, each named by a binder that returns the tool method bound to an instance, such as <c>t =&gt; t.ReadFile</c>,
/// with arguments and results serialized through the given <see cref="JsonSerializerOptions"/>. Nothing enumerates the
/// type's methods, and nothing falls back to reflection-based JSON, so a trimmed or Native AOT host can use it.
/// </summary>
/// <remarks>
/// It behaves as <see cref="LocalToolSource"/> does: the name comes from the method's <see cref="ThalosToolAttribute"/>,
/// the description from its <see cref="System.ComponentModel.DescriptionAttribute"/>, a parameter typed exactly
/// <see cref="ISecurityContext"/> is bound to the calling turn's caller and left out of the schema, a parameter whose
/// shape hides a principal anywhere else is refused at construction, and each call runs on a fresh instance in a fresh
/// DI scope, disposed afterwards, whose provider is <see cref="AIFunctionArguments.Services"/>.
/// </remarks>
/// <typeparam name="TTools">The tool class; its public constructor is resolved from the container.</typeparam>
internal sealed class LocalToolSource<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTools> : IToolSource
    where TTools : class
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<Func<TTools, Delegate>> _binders;
    private readonly JsonSerializerOptions _serializerOptions;
    private IReadOnlyList<AITool>? _tools;

    /// <summary>
    /// Creates a source named <paramref name="name"/> over the methods <paramref name="binders"/> name. As with
    /// <see cref="LocalToolSource"/>'s discovery, the binders run on the first <see cref="GetToolsAsync"/>, on a probe
    /// instance, and that call throws <see cref="InvalidOperationException"/> when a binder returns a method that is not a
    /// <see cref="ThalosToolAttribute"/> method declared by <typeparamref name="TTools"/>, or that another binder already
    /// named, or when a parameter's shape contains a type assignable to <see cref="ISecurityContext"/> anywhere other
    /// than the parameter's own top-level, by-value type.
    /// </summary>
    /// <param name="name">The source name.</param>
    /// <param name="services">The container each call's scope is created from.</param>
    /// <param name="serializerOptions">The options arguments and results go through; their resolver must cover every parameter and result type.</param>
    /// <param name="binders">One per tool: the tool method of the given instance, as a method group.</param>
    /// <exception cref="ArgumentException"><typeparamref name="TTools"/> is not marked <see cref="ThalosToolTypeAttribute"/>.
    /// </exception>
    public LocalToolSource(string name, IServiceProvider services, JsonSerializerOptions serializerOptions, IReadOnlyList<Func<TTools, Delegate>> binders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolSourceName.ThrowIfInvalid(name, nameof(name));
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(binders);
        if (!typeof(TTools).IsDefined(typeof(ThalosToolTypeAttribute), inherit: false))
        {
            throw new ArgumentException($"Type '{typeof(TTools).FullName}' is not marked [ThalosToolType].", nameof(binders));
        }

        Name = name;
        _services = services;
        _serializerOptions = serializerOptions;
        _binders = binders;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct)
    {
        _tools ??= Create();
        return new(Result<IReadOnlyList<AITool>, AgentError>.Success(_tools));
    }

    private List<AITool> Create()
    {
        var tools = new List<AITool>(_binders.Count);
        var walk = new LocalToolSourceBinding.ShapeWalk();
        var seen = new HashSet<MethodInfo>();
        using var probeScope = _services.CreateScope();
        var probe = ActivatorUtilities.CreateInstance<TTools>(probeScope.ServiceProvider);
        foreach (var binder in _binders)
        {
            var bound = binder(probe) ?? throw new InvalidOperationException($"A binder for '{typeof(TTools).FullName}' returned null.");
            var method = ToolMethod(bound, out var attribute);
            if (!seen.Add(method))
            {
                throw new InvalidOperationException($"Method '{typeof(TTools).FullName}.{method.Name}' is named by more than one binder.");
            }

            foreach (var parameter in method.GetParameters())
            {
                if (LocalToolSourceBinding.DescribeSecurityContextShapeViolation(typeof(TTools), method, parameter, walk, _serializerOptions) is { } violation)
                {
                    throw new InvalidOperationException(violation);
                }
            }

            var options = LocalToolSourceBinding.Options(attribute.Name ?? method.Name, description: null, _serializerOptions);
            var probeFunction = AIFunctionFactory.Create(bound, options);
            tools.Add(method.IsStatic ? probeFunction : new ScopedTool(_services, binder, options, probeFunction));
        }

        return tools;
    }

    /// <summary>The <see cref="ThalosToolAttribute"/> method of <typeparamref name="TTools"/> that <paramref name="bound"/> calls.</summary>
    private static MethodInfo ToolMethod(Delegate bound, out ThalosToolAttribute attribute)
    {
        var method = bound.Method;
        if (method.DeclaringType != typeof(TTools) || method.GetCustomAttribute<ThalosToolAttribute>() is not { } found)
        {
            throw new InvalidOperationException(
                $"A binder for '{typeof(TTools).FullName}' must return one of its [ThalosTool] methods as a method group; it returned '{method.DeclaringType?.FullName}.{method.Name}'.");
        }

        attribute = found;
        return method;
    }

    /// <summary>Metadata from the probe function; a fresh scope and instance per invocation, disposed after the call.</summary>
    private sealed class ScopedTool(IServiceProvider root, Func<TTools, Delegate> binder, AIFunctionFactoryOptions options, AIFunction probe)
        : DelegatingAIFunction(probe)
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            LocalToolSourceBinding.InvokeScopedAsync(
                root, sp => ActivatorUtilities.CreateInstance<TTools>(sp), instance => AIFunctionFactory.Create(binder(instance), options), arguments, cancellationToken);
    }
}
