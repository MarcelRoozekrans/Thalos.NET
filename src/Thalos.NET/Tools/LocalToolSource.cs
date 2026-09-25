using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Runtime;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tools;

/// <summary>
/// In-process tools discovered from <see cref="ThalosToolTypeAttribute"/> classes. Each invocation runs in a
/// fresh DI scope so scoped dependencies (DbContexts, repositories) are never stale; the scope's provider is also
/// exposed to the tool via <see cref="AIFunctionArguments.Services"/>.
/// </summary>
[RequiresUnreferencedCode("Discovers tool methods via reflection.")]
[RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
public sealed class LocalToolSource : IToolSource
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<Type> _toolTypes;
    private IReadOnlyList<AITool>? _tools;

    private const BindingFlags ToolMethodFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Creates a source named <paramref name="name"/> over <paramref name="toolTypes"/>; method discovery is deferred to the first <see cref="GetToolsAsync"/>.</summary>
    /// <exception cref="ArgumentException">
    /// A type in <paramref name="toolTypes"/> is not marked <see cref="ThalosToolTypeAttribute"/>, or a
    /// <see cref="ThalosToolAttribute"/> method declares a parameter whose model-deserializable shape contains a
    /// type assignable to <see cref="ISecurityContext"/> anywhere other than the parameter's own top-level,
    /// by-value type. See <see cref="DescribeSecurityContextShapeViolation"/> for what "shape" covers.
    /// </exception>
    public LocalToolSource(string name, IServiceProvider services, IReadOnlyList<Type> toolTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolSourceName.ThrowIfInvalid(name, nameof(name));
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(toolTypes);
        foreach (var type in toolTypes)
        {
            if (!type.IsDefined(typeof(ThalosToolTypeAttribute), inherit: false))
            {
                throw new ArgumentException($"Type '{type.FullName}' is not marked [ThalosToolType].", nameof(toolTypes));
            }

            foreach (var method in type.GetMethods(ToolMethodFlags))
            {
                if (!Attribute.IsDefined(method, typeof(ThalosToolAttribute)))
                {
                    continue;
                }

                foreach (var parameter in method.GetParameters())
                {
                    if (DescribeSecurityContextShapeViolation(type, method, parameter) is { } violation)
                    {
                        throw new ArgumentException(violation, nameof(toolTypes));
                    }
                }
            }
        }

        Name = name;
        _services = services;
        _toolTypes = toolTypes;
    }

    /// <summary>
    /// Describes why a <see cref="ThalosToolAttribute"/> parameter's model-deserializable shape contains a type
    /// assignable to <see cref="ISecurityContext"/> anywhere other than the parameter's own top-level, by-value
    /// type, or null when the parameter is clean. <see cref="Options"/> binds the caller only when a parameter's
    /// own type is exactly <see cref="ISecurityContext"/> passed by value; every other shape — a concrete
    /// principal, a derived interface, a nullable struct principal, a principal nested in an array or another
    /// generic type argument or a DTO property at any depth, or a principal passed <c>ref</c>/<c>in</c>/<c>out</c>
    /// — falls through to ordinary model-supplied binding instead. That exposes the principal, or the part of the
    /// shape that carries it, in the JSON schema and accepts a model-forged value, or, when the reflection
    /// marshaller cannot construct the shape (an array or DTO of interfaces), throws at call time. The caller
    /// throws the returned message as an <see cref="ArgumentException"/> against its own <c>toolTypes</c>
    /// parameter, so CA2208 sees a paramName that actually belongs to the throwing method.
    /// </summary>
    private static string? DescribeSecurityContextShapeViolation(Type toolType, MethodInfo method, ParameterInfo parameter)
    {
        var isByRef = parameter.ParameterType.IsByRef;
        var effectiveType = isByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;

        if (!isByRef && effectiveType == typeof(ISecurityContext))
        {
            return null; // the one bound shape: exactly ISecurityContext, passed by value
        }

        var offendingPath = FindSecurityContextPath(effectiveType, []);
        if (offendingPath is null)
        {
            return null;
        }

        return $"Method '{toolType.FullName}.{method.Name}' parameter '{parameter.Name}' has a shape that contains a "
            + $"type assignable to ISecurityContext at '{parameter.Name}{offendingPath}'. Only a parameter whose own "
            + "declared type is exactly ISecurityContext, passed by value, is bound to the turn; declare it that "
            + "way, or remove ISecurityContext from this parameter's shape.";
    }

    /// <summary>
    /// Walks <paramref name="type"/>'s model-deserializable shape depth-first for a type assignable to
    /// <see cref="ISecurityContext"/>, unwrapping <see cref="Nullable{T}"/>, array element types and generic type
    /// arguments, and otherwise descending into public instance properties (a DTO). Returns the path to the first
    /// match found, relative to the caller's own path prefix, e.g. <c>".Who"</c> for a property or <c>"[]"</c> for a
    /// collection element, or an empty string when <paramref name="type"/> itself matches; null when none exists.
    /// <paramref name="visiting"/> is the set of types on the current path, guarding against a DTO cycle.
    /// </summary>
    private static string? FindSecurityContextPath(Type type, HashSet<Type> visiting)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return FindSecurityContextPath(underlying, visiting);
        }

        if (typeof(ISecurityContext).IsAssignableFrom(type))
        {
            return string.Empty;
        }

        if (IsOpaqueLeaf(type) || !visiting.Add(type))
        {
            return null;
        }

        try
        {
            if (type.IsArray)
            {
                var elementPath = FindSecurityContextPath(type.GetElementType()!, visiting);
                return elementPath is null ? null : "[]" + elementPath;
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    if (FindSecurityContextPath(argument, visiting) is { } argumentPath)
                    {
                        return "[]" + argumentPath;
                    }
                }

                return null;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (FindSecurityContextPath(property.PropertyType, visiting) is { } propertyPath)
                {
                    return "." + property.Name + propertyPath;
                }
            }

            return null;
        }
        finally
        {
            visiting.Remove(type);
        }
    }

    /// <summary>
    /// Types the shape walk in <see cref="FindSecurityContextPath"/> never descends into: primitives, common BCL
    /// value types, enums, and the handful of reference types MAF or <see cref="LocalToolSource"/> already bind
    /// specially (<see cref="object"/> itself, <see cref="CancellationToken"/>, <see cref="IServiceProvider"/>,
    /// <see cref="AIFunctionArguments"/>) rather than deserializing from model arguments.
    /// </summary>
    private static bool IsOpaqueLeaf(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(object)
        || type == typeof(string)
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(DateOnly)
        || type == typeof(TimeOnly)
        || type == typeof(TimeSpan)
        || type == typeof(Guid)
        || type == typeof(CancellationToken)
        || type == typeof(IServiceProvider)
        || type == typeof(AIFunctionArguments);

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct)
    {
        _tools ??= Discover();
        return new(Result<IReadOnlyList<AITool>, AgentError>.Success(_tools));
    }

    private List<AITool> Discover()
    {
        var tools = new List<AITool>();
        using var probeScope = _services.CreateScope();

        foreach (var type in _toolTypes)
        {
            var probe = ActivatorUtilities.CreateInstance(probeScope.ServiceProvider, type);
            foreach (var method in type.GetMethods(ToolMethodFlags))
            {
                if (method.GetCustomAttribute<ThalosToolAttribute>() is not { } attr)
                {
                    continue;
                }

                var toolName = attr.Name ?? method.Name;
                var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
                var probeFunction = AIFunctionFactory.Create(method, method.IsStatic ? null : probe, Options(toolName, description));
                tools.Add(method.IsStatic ? probeFunction : new ScopedTool(_services, type, method, probeFunction));
            }
        }

        return tools;
    }

    /// <summary>
    /// Options shared by the probe and bound <see cref="AIFunctionFactory.Create(MethodInfo, object?, AIFunctionFactoryOptions?)"/>
    /// calls for a tool method. A parameter typed exactly <see cref="ISecurityContext"/> is bound to the calling
    /// turn's <see cref="TurnScope.Caller"/>, falling back to <see cref="AnonymousSecurityContext.Instance"/> outside
    /// a turn, and is excluded from the JSON schema so the model can never see or forge it.
    /// </summary>
    /// <remarks>
    /// The match is exact-type, not assignability: <see cref="DescribeSecurityContextShapeViolation"/> rejects, eagerly at
    /// construction, any parameter whose shape carries an <see cref="ISecurityContext"/>-assignable type anywhere
    /// other than this one exact, by-value spot, because the ambient caller cannot be guaranteed to be that other
    /// concrete type. A parameter typed as the non-specific <see cref="object"/> is not, and cannot be, treated
    /// specially here — nothing distinguishes it from a tool that legitimately wants free-form model input, so it is
    /// bound and schema'd exactly like any other <see cref="object"/> parameter.
    /// </remarks>
    private static AIFunctionFactoryOptions Options(string name, string? description) => new()
    {
        Name = name,
        Description = description,
        ConfigureParameterBinding = static p => p.ParameterType == typeof(ISecurityContext)
            ? new AIFunctionFactoryOptions.ParameterBindingOptions
            {
                ExcludeFromSchema = true,
                BindParameter = static (_, _) => TurnScope.Current?.Caller ?? AnonymousSecurityContext.Instance,
            }
            : default,
    };

    /// <summary>Metadata from the probe function; a fresh scope + instance per invocation, disposed after the call.</summary>
    private sealed class ScopedTool(IServiceProvider root, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type toolType, MethodInfo method, AIFunction probe)
        : DelegatingAIFunction(probe)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var scope = root.CreateAsyncScope();
            await using var _ = scope.ConfigureAwait(false);
            var instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, toolType);
            try
            {
                var bound = AIFunctionFactory.Create(method, instance, Options(Name, Description));
                var scopedArguments = new AIFunctionArguments(arguments, StringComparer.Ordinal) { Services = scope.ServiceProvider, Context = arguments.Context };
                return await bound.InvokeAsync(scopedArguments, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                switch (instance)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
        }
    }
}
